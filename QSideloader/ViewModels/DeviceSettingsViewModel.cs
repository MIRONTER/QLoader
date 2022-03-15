using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
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

        this.WhenActivated(disposables =>
        {
            //TODO: on device connect and disconnect events handling
            if (_adbService.ValidateDeviceConnection())
            {
                IsDeviceConnected = true;
                RefreshRates = _adbService.Device!.Product switch
                {
                    "hollywood" => new[]{"Auto", "72", "90", "120"},
                    "monterey" => new[]{"Auto", "60", "72"},
                    _ => RefreshRates
                };
                Task.Run(LoadCurrentSettings);
            }
            _adbService.DeviceOnline += OnDeviceOnline;
            _adbService.DeviceOffline += OnDeviceOffline;
            Disposable
                .Create(() => { })
                .DisposeWith(disposables);
        });
    }

    private void OnDeviceOffline(object? sender, EventArgs e)
    {
        IsDeviceConnected = false;
    }

    private void OnDeviceOnline(object? sender, EventArgs e)
    {
        IsDeviceConnected = true;
        LoadCurrentSettings();
    }

    [Reactive] public bool IsDeviceConnected { get; private set; }
    [Reactive] public string[] RefreshRates { get; private set; } = Array.Empty<string>();
    [Reactive] public string? SelectedRefreshRate { get; set; }
    public string[] GpuLevels { get; } = {"Auto", "0", "1", "2", "3", "4"};
    [Reactive] public string? SelectedGpuLevel { get; set; }
    public string[] CpuLevels { get; } = {"Auto", "0", "1", "2", "3", "4"};
    [Reactive] public string? SelectedCpuLevel { get; set; }
    public string[] TextureSizes { get; } = {"Auto", "512", "768", "1024", "1216", "1440", "1536", "2048", "2560", "3072"};
    [Reactive] public string? SelectedTextureSize { get; set; }
    //[Reactive] public string ResolutionTextBoxText { get; set; } = "";
    public ReactiveCommand<Unit, Unit> ApplySettings { get; }
    public ReactiveCommand<Unit, Unit> MountStorage { get; }
    public ReactiveCommand<Unit, Unit> LaunchHiddenSettings { get; }
    public ViewModelActivator Activator { get; }

    private void LoadCurrentSettings()
    {
        if (!_adbService.ValidateDeviceConnection()) return;
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
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (refreshRate != 0)
            {
                SelectedRefreshRate = refreshRate.ToString();
                Log.Debug("Refresh rate: {RefreshRate}", refreshRate);
            }
            if (gpuLevel != 0)
            {
                SelectedGpuLevel = gpuLevel.ToString();
                Log.Debug("GPU level: {GpuLevel}", gpuLevel);
            }
            if (cpuLevel != 0)
            {
                SelectedCpuLevel = cpuLevel.ToString();
                Log.Debug("CPU level: {CpuLevel}", cpuLevel);
            }
            // ReSharper disable once InvertIf
            if (textureHeight != 0 && textureWidth != 0)
            {
                SelectedTextureSize = textureWidth.ToString();
                Log.Debug("Default texture size: {TextureSize}", textureWidth);
            }
        });
    }

    private IObservable<Unit> ApplySettingsImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.ValidateDeviceConnection()) return;
            if (SelectedRefreshRate == "Auto")
            {
                _adbService.Device!.RunShellCommand(
                    $"setprop debug.oculus.refreshRate \"\"", true);
                Log.Information("Reset refresh rate to Auto");
            }
            else if (int.TryParse(SelectedRefreshRate, out var refreshRate))
            {
                _adbService.Device!.RunShellCommand(
                    $"setprop debug.oculus.refreshRate {refreshRate}", true);
                Log.Information("Set refresh rate: {RefreshRate} Hz", refreshRate);
            }
            if (SelectedGpuLevel == "Auto")
            {
                _adbService.Device!.RunShellCommand(
                    $"setprop debug.oculus.gpuLevel \"\"", true);
                Log.Information("Reset GPU level to Auto");
            }
            else if (int.TryParse(SelectedGpuLevel, out var gpuLevel))
            {
                _adbService.Device!.RunShellCommand(
                    $"setprop debug.oculus.gpuLevel {gpuLevel}", true);
                Log.Information("Set GPU level: {GpuLevel}", gpuLevel);
            }
            if (SelectedCpuLevel == "Auto")
            {
                _adbService.Device!.RunShellCommand(
                    $"setprop debug.oculus.cpuLevel \"\"", true);
                Log.Information("Reset CPU level to Auto");
            }
            else if (int.TryParse(SelectedCpuLevel, out var cpuLevel))
            {
                _adbService.Device!.RunShellCommand(
                    $"setprop debug.oculus.cpuLevel {cpuLevel}", true);
                Log.Information("Set CPU level: {CpuLevel}", cpuLevel);
            }

            if (SelectedTextureSize == "Auto")
            {
                _adbService.Device!.RunShellCommand(
                    $"setprop debug.oculus.textureWidth \"\"", true);
                _adbService.Device!.RunShellCommand(
                    $"setprop debug.oculus.textureHeight \"\"", true);
                Log.Information("Reset texture resolution to Auto");
            }
            else if (SelectedTextureSize != null)
            {
                ResolutionValueToDimensions(SelectedTextureSize, out var width, out var height);
                _adbService.Device!.RunShellCommand($"setprop debug.oculus.textureWidth {width}", true);
                _adbService.Device!.RunShellCommand($"setprop debug.oculus.textureHeight {height}", true);
                Log.Information("Set texture resolution Width:{Width} Height:{Height}", width, height);
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

    private IObservable<Unit> MountStorageImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.ValidateDeviceConnection()) return;
            _adbService.Device!.RunShellCommand("svc usb setFunctions mtp true", true);
            Log.Information("Mounted device storage");
        });
    }
    private IObservable<Unit> LaunchHiddenSettingsImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.ValidateDeviceConnection()) return;
            _adbService.Device!.RunShellCommand("am start -a android.intent.action.VIEW -d com.oculus.tv -e uri com.android.settings/.DevelopmentSettings com.oculus.vrshell/.MainActivity", true);
            Log.Information("Launched hidden settings");
        });
    }
}