using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using Avalonia.Threading;
using QSideloader.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using ReactiveUI.Validation.Extensions;
using Serilog;

namespace QSideloader.ViewModels;

public class DeviceSettingsViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly AdbService _adbService;

    public DeviceSettingsViewModel()
    {
        _adbService = ServiceContainer.AdbService;
        Activator = new ViewModelActivator();
        ApplySettings = ReactiveCommand.CreateFromObservable(ApplySettingsImpl, this.IsValid());
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
            Task.Run(Initialize);

            _adbService.DeviceChange.Subscribe(OnDeviceChange).DisposeWith(disposables);
        });
    }

    private void OnDeviceChange(AdbService.AdbDevice device)
    {
        switch (device.State)
        {
            case DeviceState.Online:
                IsDeviceConnected = true;
                RefreshRates = device.Product switch
                {
                    "hollywood" => new[] {"Auto (recommended)", "72", "90", "120"},
                    "monterey" => new[] {"Auto (recommended)", "60", "72"},
                    _ => RefreshRates
                };
                LoadCurrentSettings();
                break;
            case DeviceState.Offline:
                IsDeviceConnected = false;
                break;
        }
    }

    [Reactive] public bool IsDeviceConnected { get; private set; }
    [Reactive] public string[] RefreshRates { get; private set; } = Array.Empty<string>();
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

    private void Initialize()
    {
        if (!_adbService.CheckDeviceConnection()) return;
        OnDeviceChange(_adbService.Device!);
    }
    
    private void LoadCurrentSettings()
    {
        Log.Debug("Loading device settings");
#pragma warning disable CA1806
        int.TryParse(_adbService.Device!.RunShellCommand("getprop debug.oculus.refreshRate"),
            out var refreshRate);
        int.TryParse(_adbService.Device!.RunShellCommand("getprop debug.oculus.gpuLevel"),
            out var gpuLevel);
        int.TryParse(_adbService.Device!.RunShellCommand("getprop debug.oculus.cpuLevel"),
            out var cpuLevel);
        int.TryParse(_adbService.Device!.RunShellCommand("getprop debug.oculus.textureWidth"),
            out var textureWidth);
        int.TryParse(_adbService.Device!.RunShellCommand("getprop debug.oculus.textureHeight"),
            out var textureHeight);
#pragma warning restore CA1806
        var currentUsername = _adbService.Device.RunShellCommand("settings get global username");
        Dispatcher.UIThread.InvokeAsync(() =>
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
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnection())
            {
                Log.Warning("ApplySettingsImpl: no device connection!");
                return;
            }

            // Check if any option is selected and it differs from current setting
            if (SelectedRefreshRate is not null &&
                (CurrentRefreshRate is null || SelectedRefreshRate != CurrentRefreshRate))
            {
                if (SelectedRefreshRate.Contains("Auto") && CurrentRefreshRate is not null)
                {
                    _adbService.Device!.RunShellCommand(
                        $"setprop debug.oculus.refreshRate \"\"", true);
                    CurrentRefreshRate = null;
                    Log.Information("Reset refresh rate to Auto");
                }
                else if (int.TryParse(SelectedRefreshRate, out var refreshRate))
                {
                    _adbService.Device!.RunShellCommand(
                        $"setprop debug.oculus.refreshRate {refreshRate}", true);
                    CurrentRefreshRate = SelectedRefreshRate;
                    Log.Information("Set refresh rate: {RefreshRate} Hz", refreshRate);
                }
            }

            if (SelectedGpuLevel is not null && (CurrentGpuLevel is null || SelectedGpuLevel != CurrentGpuLevel))
            {
                if (SelectedGpuLevel.Contains("Auto") && CurrentGpuLevel is not null)
                {
                    _adbService.Device!.RunShellCommand(
                        $"setprop debug.oculus.gpuLevel \"\"", true);
                    CurrentGpuLevel = null;
                    Log.Information("Reset GPU level to Auto");
                }
                else if (int.TryParse(SelectedGpuLevel, out var gpuLevel))
                {
                    _adbService.Device!.RunShellCommand(
                        $"setprop debug.oculus.gpuLevel {gpuLevel}", true);
                    CurrentGpuLevel = SelectedGpuLevel;
                    Log.Information("Set GPU level: {GpuLevel}", gpuLevel);
                }
            }

            if (SelectedCpuLevel is not null && (CurrentCpuLevel is null || SelectedCpuLevel != CurrentCpuLevel))
            {
                if (SelectedCpuLevel.Contains("Auto") && CurrentCpuLevel is not null)
                {
                    _adbService.Device!.RunShellCommand(
                        $"setprop debug.oculus.cpuLevel \"\"", true);
                    CurrentCpuLevel = null;
                    Log.Information("Reset CPU level to Auto");
                }
                else if (int.TryParse(SelectedCpuLevel, out var cpuLevel))
                {
                    _adbService.Device!.RunShellCommand(
                        $"setprop debug.oculus.cpuLevel {cpuLevel}", true);
                    CurrentCpuLevel = SelectedCpuLevel;
                    Log.Information("Set CPU level: {CpuLevel}", cpuLevel);
                }
            }

            if (SelectedTextureSize is not null &&
                (CurrentTextureSize is null || SelectedTextureSize != CurrentTextureSize))
            {
                if (SelectedTextureSize.Contains("Auto") && CurrentTextureSize is not null)
                {
                    _adbService.Device!.RunShellCommand(
                        $"setprop debug.oculus.textureWidth \"\"", true);
                    _adbService.Device.RunShellCommand(
                        $"setprop debug.oculus.textureHeight \"\"", true);
                    CurrentTextureSize = null;
                    Log.Information("Reset texture resolution to Auto");
                }
                else if (SelectedTextureSize != null)
                {
                    ResolutionValueToDimensions(SelectedTextureSize, out var width, out var height);
                    _adbService.Device!.RunShellCommand($"setprop debug.oculus.textureWidth {width}", true);
                    _adbService.Device.RunShellCommand($"setprop debug.oculus.textureHeight {height}", true);
                    CurrentTextureSize = SelectedTextureSize;
                    Log.Information("Set texture resolution Width:{Width} Height:{Height}", width, height);
                }
            }

            // ReSharper disable once InvertIf
            if (UsernameTextBoxText is not null && (CurrentUsername is null || UsernameTextBoxText != CurrentUsername))
            {
                if (string.IsNullOrEmpty(UsernameTextBoxText) && CurrentUsername is not null)
                {
                    _adbService.Device!.RunShellCommand("settings put global username null");
                    Log.Information("Reset username");
                }
                else if (UsernameTextBoxText is not null && IsValidUsername(UsernameTextBoxText))
                {
                    _adbService.Device!.RunShellCommand($"settings put global username {UsernameTextBoxText}");
                    Log.Information("Set username: {Username}", UsernameTextBoxText);
                }
            }
        });
    }

    /*private static bool TryParseResolutionString(string? input, out int width, out int height)
    {
        if (input is null)
        {
            width = 0;
            height = 0;
            return false;
        }
        
        if (input.Length is 3 or 4 && int.TryParse(input, out var value))
        {
            switch (value)
            {
                case 512:
                    width = value;
                    height = 563;
                    return true;
                case 768:
                    width = value;
                    height = 845;
                    return true;
                case 1024:
                    width = value;
                    height = 1127;
                    return true;
                case 1216:
                    width = value;
                    height = 1344;
                    return true;
                case 1440:
                    width = value;
                    height = 1584;
                    return true;
                case 1536:
                    width = value;
                    height = 1590;
                    return true;
                case 2048:
                    width = value;
                    height = 2253;
                    return true;
                case 2560:
                    width = value;
                    height = 2816;
                    return true;
                case 3072:
                    width = value;
                    height = 3380;
                    return true;
                default:
                    width = 0;
                    height = 0;
                    return false;
            }
        }

        if (input.Contains('x'))
        {
            const string resolutionStringPattern = @"^(\d{3,4})x(\d{3,4})$";
            var match = Regex.Match(input, resolutionStringPattern);
            if (match.Success)
            {
                width = int.Parse(match.Groups[1].ToString());
                height = int.Parse(match.Groups[2].ToString());
                return true;
            }
        }

        width = 0;
        height = 0;
        return false;
    }*/

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
        // Regex for checking username against Oculus username requirements:
        // https://support.oculus.com/articles/accounts/account-settings-and-management/change-oculus-username/
        const string usernameCheckPattern = @"^(?![-_])(?!.*--)(?!.*__)[\w-]{2,20}$";
        return Regex.IsMatch(username, usernameCheckPattern);
    }

    private IObservable<Unit> MountStorageImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnection()) return;
            _adbService.Device!.RunShellCommand("svc usb setFunctions mtp true", true);
            Log.Information("Mounted device storage");
        });
    }

    private IObservable<Unit> LaunchHiddenSettingsImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnection()) return;
            _adbService.Device!.RunShellCommand(
                "am start -a android.intent.action.VIEW -d com.oculus.tv -e uri com.android.settings/.DevelopmentSettings com.oculus.vrshell/.MainActivity",
                true);
            Log.Information("Launched hidden settings");
        });
    }
}