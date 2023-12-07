using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AdvancedSharpAdbClient.Models;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Services;
using QSideloader.Utilities;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using ReactiveUI.Validation.Extensions;
using Serilog;

namespace QSideloader.ViewModels;

public partial class DeviceSettingsViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly AdbService _adbService;
    private readonly SettingsData _sideloaderSettings;

    public DeviceSettingsViewModel()
    {
        PullMedia = ReactiveCommand.CreateFromTask(PullMediaImpl);
        _adbService = AdbService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        Activator = new ViewModelActivator();
        ApplySettings = ReactiveCommand.CreateFromObservable(ApplySettingsImpl, this.IsValid());
        ApplySettings.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error applying device settings");
            Globals.ShowErrorNotification(ex, Resources.ErrorApplyingDeviceSettings);
        });
        MountStorage = ReactiveCommand.CreateFromObservable(MountStorageImpl);
        LaunchHiddenSettings = ReactiveCommand.CreateFromObservable(LaunchHiddenSettingsImpl);
        // this.ValidationRule(viewModel => viewModel.ResolutionTextBoxText,
        //     x => string.IsNullOrEmpty(x) || x == "0" || TryParseResolutionString(x, out _, out _), 
        //     "Invalid input format");
        this.ValidationRule(viewModel => viewModel.UsernameTextBoxText,
            x => string.IsNullOrEmpty(x) || IsValidUsername(x),
            "Invalid username");

        this.WhenActivated(disposables =>
        {
            Task.Run(OnActivatedAsync).DisposeWith(disposables);

            _adbService.WhenDeviceStateChanged.Subscribe(x => Task.Run(() => Task.FromResult(OnDeviceStateChangedAsync(x))))
                .DisposeWith(disposables);
        });
    }

    public ReactiveCommand<Unit, Unit> PullMedia { get; set; }

    [Reactive] public bool IsDeviceConnected { get; private set; }
    [Reactive] public ObservableCollection<string> RefreshRates { get; private set; } = new();
    [Reactive] public string? SelectedRefreshRate { get; set; }
    private string? CurrentRefreshRate { get; set; }
    public string[] GpuLevels { get; } = {"Auto (recommended)", "0", "1", "2", "3", "4"};
    [Reactive] public string? SelectedGpuLevel { get; set; }
    private string? CurrentGpuLevel { get; set; }
    public string[] CpuLevels { get; } = {"Auto (recommended)", "0", "1", "2", "3", "4"};
    [Reactive] public string? SelectedCpuLevel { get; set; }
    private string? CurrentCpuLevel { get; set; }

    public string[] TextureSizes { get; } =
        {"Auto (recommended)", "512", "768", "1024", "1216", "1440", "1536", "2048", "2560", "3072"};

    [Reactive] public string? SelectedTextureSize { get; set; }

    private string? CurrentTextureSize { get; set; }

    //[Reactive] public string ResolutionTextBoxText { get; set; } = "";
    [Reactive] public string? UsernameTextBoxText { get; set; }
    private string? CurrentUsername { get; set; }
    public ReactiveCommand<Unit, Unit> ApplySettings { get; }
    public ReactiveCommand<Unit, Unit> MountStorage { get; }
    public ReactiveCommand<Unit, Unit> LaunchHiddenSettings { get; }
    public ViewModelActivator Activator { get; }

    private async Task OnDeviceStateChangedAsync(DeviceState state)
    {
        switch (state)
        {
            case DeviceState.Online:
                IsDeviceConnected = true;
                RefreshRates = new ObservableCollection<string> {"Auto (recommended)"};
                foreach (var refreshRate in _adbService.Device!.SupportedRefreshRates)
                {
                    RefreshRates.Add(refreshRate.ToString());
                }

                try
                {
                    await LoadCurrentSettingsAsync();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Failed to load current device settings");
                    Globals.ShowErrorNotification(e, Resources.FailedToLoadDeviceSettings);
                }

                break;
            case DeviceState.Offline:
                IsDeviceConnected = false;
                break;
        }
    }

    private async Task OnActivatedAsync()
    {
        if (await _adbService.CheckDeviceConnectionAsync())
            await OnDeviceStateChangedAsync(DeviceState.Online);
        else
            IsDeviceConnected = false;
    }

    private async Task PullMediaImpl()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow!;
            // Default to last used location or desktop
            var defaultLocation = await mainWindow.StorageProvider.TryGetFolderFromPathAsync(
                !string.IsNullOrEmpty(_sideloaderSettings.LastMediaPullLocation)
                    ? _sideloaderSettings.LastMediaPullLocation
                    : Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            var selectedLocations = await mainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Resources.SelectDestinationFolder,
                SuggestedStartLocation = defaultLocation,
                AllowMultiple = false
            });
            if (selectedLocations.Count > 0)
            {
                var path = selectedLocations[0].TryGetLocalPath();
                if (!Directory.Exists(path))
                {
                    Log.Error("Selected path for media pull does not exist: {Path}", path);
                    return;
                }

                _sideloaderSettings.LastMediaPullLocation = path;
                Globals.MainWindowViewModel!.AddTask(new TaskOptions
                {
                    Type = TaskType.PullMedia,
                    Path = path + Path.DirectorySeparatorChar + "OculusMedia"
                });
            }
            else
            {
                Log.Information("No folder selected for media pull");
            }
        }
    }

    private async Task LoadCurrentSettingsAsync()
    {
        Log.Debug("Loading device settings");
#pragma warning disable CA1806
        int.TryParse(await _adbService.Device!.RunShellCommandAsync("getprop debug.oculus.refreshRate"),
            out var refreshRate);
        int.TryParse(await _adbService.Device!.RunShellCommandAsync("getprop debug.oculus.gpuLevel"),
            out var gpuLevel);
        int.TryParse(await _adbService.Device!.RunShellCommandAsync("getprop debug.oculus.cpuLevel"),
            out var cpuLevel);
        int.TryParse(await _adbService.Device!.RunShellCommandAsync("getprop debug.oculus.textureWidth"),
            out var textureWidth);
        int.TryParse(await _adbService.Device!.RunShellCommandAsync("getprop debug.oculus.textureHeight"),
            out var textureHeight);
#pragma warning restore CA1806
        var currentUsername = await _adbService.Device!.RunShellCommandAsync("settings get global username");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (refreshRate != 0)
            {
                CurrentRefreshRate = refreshRate.ToString();
                SelectedRefreshRate = refreshRate.ToString();
                Log.Debug("Refresh rate: {RefreshRate}", refreshRate);
            }
            else
            {
                CurrentRefreshRate = null;
                SelectedRefreshRate = null;
            }

            if (gpuLevel != 0)
            {
                CurrentGpuLevel = gpuLevel.ToString();
                SelectedGpuLevel = gpuLevel.ToString();
                Log.Debug("GPU level: {GpuLevel}", gpuLevel);
            }
            else
            {
                CurrentGpuLevel = null;
                SelectedGpuLevel = null;
            }

            if (cpuLevel != 0)
            {
                CurrentCpuLevel = cpuLevel.ToString();
                SelectedCpuLevel = cpuLevel.ToString();
                Log.Debug("CPU level: {CpuLevel}", cpuLevel);
            }
            else
            {
                CurrentCpuLevel = null;
                SelectedCpuLevel = null;
            }

            if (textureHeight != 0 && textureWidth != 0)
            {
                CurrentTextureSize = textureWidth.ToString();
                SelectedTextureSize = textureWidth.ToString();
                Log.Debug("Default texture size: {TextureSize}", textureWidth);
            }
            else
            {
                CurrentTextureSize = null;
                SelectedTextureSize = null;
            }

            // ReSharper disable once InvertIf
            // Comparison to literal is intentional
            if (currentUsername != "null")
            {
                CurrentUsername = currentUsername;
                UsernameTextBoxText = currentUsername;
            }
            else
            {
                CurrentUsername = null;
                UsernameTextBoxText = null;
            }
        });
    }

    // TODO: too many conditions, see if this can be simplified
    private IObservable<Unit> ApplySettingsImpl()
    {
        return Observable.StartAsync(async () =>
        {
            if (!_adbService.IsDeviceConnected)
            {
                Log.Warning("DeviceSettingsViewModel.ApplySettingsImpl: no device connection!");
                IsDeviceConnected = false;
                return;
            }

            // Check if any option is selected and it differs from current setting
            if (SelectedRefreshRate is not null && SelectedRefreshRate != CurrentRefreshRate)
            {
                if (SelectedRefreshRate.Contains("Auto") && CurrentRefreshRate is not null)
                {
                    await _adbService.Device!.RunShellCommandAsync(
                        "setprop debug.oculus.refreshRate \"\"", true);
                    CurrentRefreshRate = null;
                    Log.Information("Reset refresh rate to Auto");
                }
                else if (int.TryParse(SelectedRefreshRate, out var refreshRate))
                {
                    await _adbService.Device!.RunShellCommandAsync(
                        $"setprop debug.oculus.refreshRate {refreshRate}", true);
                    CurrentRefreshRate = SelectedRefreshRate;
                    Log.Information("Set refresh rate: {RefreshRate} Hz", refreshRate);
                }
            }

            if (SelectedGpuLevel is not null && SelectedGpuLevel != CurrentGpuLevel)
            {
                if (SelectedGpuLevel.Contains("Auto") && CurrentGpuLevel is not null)
                {
                    await _adbService.Device!.RunShellCommandAsync(
                        "setprop debug.oculus.gpuLevel \"\"", true);
                    CurrentGpuLevel = null;
                    Log.Information("Reset GPU level to Auto");
                }
                else if (int.TryParse(SelectedGpuLevel, out var gpuLevel))
                {
                    await _adbService.Device!.RunShellCommandAsync(
                        $"setprop debug.oculus.gpuLevel {gpuLevel}", true);
                    CurrentGpuLevel = SelectedGpuLevel;
                    Log.Information("Set GPU level: {GpuLevel}", gpuLevel);
                }
            }

            if (SelectedCpuLevel is not null && SelectedCpuLevel != CurrentCpuLevel)
            {
                if (SelectedCpuLevel.Contains("Auto") && CurrentCpuLevel is not null)
                {
                    await _adbService.Device!.RunShellCommandAsync(
                        "setprop debug.oculus.cpuLevel \"\"", true);
                    CurrentCpuLevel = null;
                    Log.Information("Reset CPU level to Auto");
                }
                else if (int.TryParse(SelectedCpuLevel, out var cpuLevel))
                {
                    await _adbService.Device!.RunShellCommandAsync(
                        $"setprop debug.oculus.cpuLevel {cpuLevel}", true);
                    CurrentCpuLevel = SelectedCpuLevel;
                    Log.Information("Set CPU level: {CpuLevel}", cpuLevel);
                }
            }

            if (SelectedTextureSize is not null && SelectedTextureSize != CurrentTextureSize)
            {
                if (SelectedTextureSize.Contains("Auto") && CurrentTextureSize is not null)
                {
                    await _adbService.Device!.RunShellCommandAsync(
                        "setprop debug.oculus.textureWidth \"\"", true);
                    await _adbService.Device!.RunShellCommandAsync(
                        "setprop debug.oculus.textureHeight \"\"", true);
                    CurrentTextureSize = null;
                    Log.Information("Reset texture resolution to Auto");
                }
                else if (!SelectedTextureSize.Contains("Auto"))
                {
                    ResolutionValueToDimensions(SelectedTextureSize, out var width, out var height);
                    await _adbService.Device!.RunShellCommandAsync($"setprop debug.oculus.textureWidth {width}", true);
                    await _adbService.Device!.RunShellCommandAsync($"setprop debug.oculus.textureHeight {height}", true);
                    CurrentTextureSize = SelectedTextureSize;
                    Log.Information("Set texture resolution Width:{Width} Height:{Height}", width, height);
                }
            }

            if (UsernameTextBoxText is not null && UsernameTextBoxText != CurrentUsername)
            {
                if (string.IsNullOrEmpty(UsernameTextBoxText) && CurrentUsername is not null)
                {
                    await _adbService.Device!.RunShellCommandAsync("settings put global username null");
                    Log.Information("Reset username");
                }
                else if (IsValidUsername(UsernameTextBoxText))
                {
                    await _adbService.Device!.RunShellCommandAsync($"settings put global username {UsernameTextBoxText}");
                    Log.Information("Set username");
                }
            }

            Log.Information("Applied device settings");
            Globals.ShowNotification(Resources.Info, Resources.AppliedDeviceSettings, NotificationType.Success,
                TimeSpan.FromSeconds(2));
        });
    }

    private static void ResolutionValueToDimensions(string input, out int width, out int height)
    {
        var value = int.Parse(input);
        switch (value)
        {
            case 512:
                width = value;
                height = 563;
                return;
            case 768:
                width = value;
                height = 845;
                return;
            case 1024:
                width = value;
                height = 1127;
                return;
            case 1216:
                width = value;
                height = 1344;
                return;
            case 1440:
                width = value;
                height = 1584;
                return;
            case 1536:
                width = value;
                height = 1590;
                return;
            case 2048:
                width = value;
                height = 2253;
                return;
            case 2560:
                width = value;
                height = 2816;
                return;
            case 3072:
                width = value;
                height = 3380;
                return;
            default:
                width = 0;
                height = 0;
                return;
        }
    }

    private static bool IsValidUsername(string username)
    {
        return UsernameRegex().IsMatch(username);
    }

    private IObservable<Unit> MountStorageImpl()
    {
        return Observable.StartAsync(async () =>
        {
            if (!_adbService.IsDeviceConnected)
            {
                Log.Warning("DeviceSettingsViewModel.MountStorageImpl: no device connection!");
                IsDeviceConnected = false;
                return;
            }

            await _adbService.Device!.RunShellCommandAsync("svc usb setFunctions mtp true", true);
            Log.Information("Mounted device storage");
            Globals.ShowNotification(Resources.Info, Resources.DeviceStorageMounted, NotificationType.Success,
                TimeSpan.FromSeconds(2));
        });
    }

    private IObservable<Unit> LaunchHiddenSettingsImpl()
    {
        return Observable.StartAsync(async () =>
        {
            if (!_adbService.IsDeviceConnected)
            {
                Log.Warning("DeviceSettingsViewModel.LaunchHiddenSettingsImpl: no device connection!");
                IsDeviceConnected = false;
                return;
            }

            await _adbService.Device!.RunShellCommandAsync(
                "am start -a android.intent.action.VIEW -d com.oculus.tv -e uri com.android.settings/.DevelopmentSettings com.oculus.vrshell/.MainActivity",
                true);
            Log.Information("Launched hidden settings");
        });
    }

    /// <summary>
    /// Regex for checking username against Oculus username requirements:
    /// https://www.meta.com/en-gb/help/quest/articles/accounts/account-settings-and-management/manage-oculus-account/
    /// </summary>
    [GeneratedRegex("^(?![-_])(?!.*--)(?!.*__)[\\w-]{2,20}$")]
    private static partial Regex UsernameRegex();
}