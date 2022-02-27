using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
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
    public DeviceSettingsViewModel()
    {
        Activator = new ViewModelActivator();
        ApplySettings = ReactiveCommand.CreateFromObservable(ApplySettingsImpl, this.IsValid());
        MountStorage = ReactiveCommand.CreateFromObservable(MountStorageImpl);
        LaunchHiddenSettings = ReactiveCommand.CreateFromObservable(LaunchHiddenSettingsImpl);
        this.ValidationRule(viewModel => viewModel.ResolutionTextBoxText,
            x => (TryParseResolutionString(x, out _, out _) || string.IsNullOrEmpty(x)), "Invalid input format");
        this.WhenActivated(disposables =>
        {
            //TODO: on device connect and disconnect events handling
            if (ServiceContainer.ADBService.ValidateDeviceConnection())
            {
                IsDeviceConnected = true;
                RefreshRates = ServiceContainer.ADBService.Device!.Product switch
                {
                    "hollywood" => new List<string> {"Default", "72", "90", "120"},
                    "monterey" => new List<string> {"Default", "60", "72"},
                    _ => RefreshRates
                };
#pragma warning disable CS4014
                LoadCurrentSettings();
#pragma warning restore CS4014
            }
            Disposable
                .Create(() => { })
                .DisposeWith(disposables);
        });
    }

    [Reactive] public bool IsDeviceConnected { get; private set; }
    [Reactive] public List<string> RefreshRates { get; private set; } = new();
    [Reactive] public string? SelectedRefreshRate { get; set; }
    public List<string> GpuLevels { get; } = new() {"Default", "0", "1", "2", "3", "4"};
    [Reactive] public string? SelectedGpuLevel { get; set; }
    public List<string> CpuLevels { get; } = new() {"Default", "0", "1", "2", "3", "4"};
    [Reactive] public string? SelectedCpuLevel { get; set; }
    [Reactive] public string ResolutionTextBoxText { get; set; } = "";
    public ReactiveCommand<Unit, Unit> ApplySettings { get; }
    public ReactiveCommand<Unit, Unit> MountStorage { get; }
    public ReactiveCommand<Unit, Unit> LaunchHiddenSettings { get; }
    public ViewModelActivator Activator { get; }

    private async Task LoadCurrentSettings()
    {
        if (!ServiceContainer.ADBService.ValidateDeviceConnection()) return;
        var refreshRate = 0;
        var gpuLevel = 0;
        var cpuLevel = 0;
        var textureWidth = 0;
        var textureHeight = 0;
        await Task.Run(() =>
        {
            int.TryParse(ServiceContainer.ADBService.Device!.RunShellCommand("getprop debug.oculus.refreshRate"), 
                out refreshRate);
            int.TryParse(ServiceContainer.ADBService.Device!.RunShellCommand("getprop debug.oculus.gpuLevel"),
                out gpuLevel);
            int.TryParse(ServiceContainer.ADBService.Device!.RunShellCommand("getprop debug.oculus.cpuLevel"),
                out cpuLevel);
            int.TryParse(ServiceContainer.ADBService.Device!.RunShellCommand("getprop debug.oculus.textureWidth"), 
                out textureWidth);
            int.TryParse(ServiceContainer.ADBService.Device!.RunShellCommand("getprop debug.oculus.textureHeight"), 
                out textureHeight);
        });
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (refreshRate != 0)
                SelectedRefreshRate = refreshRate.ToString();
            if (gpuLevel != 0)
                SelectedGpuLevel = gpuLevel.ToString();
            if (cpuLevel != 0)
                SelectedCpuLevel = cpuLevel.ToString();
            if (textureHeight != 0 && textureWidth != 0)
                ResolutionTextBoxText = $"{textureWidth}x{textureHeight}";
        });
    }

    private IObservable<Unit> ApplySettingsImpl()
    {
        return Observable.Start(() =>
        {
            if (!ServiceContainer.ADBService.ValidateDeviceConnection()) return;
            if (string.IsNullOrEmpty(SelectedRefreshRate) || SelectedRefreshRate == "Default")
            {
                ServiceContainer.ADBService.Device!.RunShellCommand(
                    $"setprop debug.oculus.refreshRate \"\"", true);
                Log.Debug("Reset refresh rate to default");
            }
            else if (int.TryParse(SelectedRefreshRate, out var refreshRate))
            {
                ServiceContainer.ADBService.Device!.RunShellCommand(
                    $"setprop debug.oculus.refreshRate {refreshRate}", true);
                Log.Debug("Set refresh rate: {RefreshRate} Hz", refreshRate);
            }
            if (string.IsNullOrEmpty(SelectedGpuLevel) || SelectedGpuLevel == "Default")
            {
                ServiceContainer.ADBService.Device!.RunShellCommand(
                    $"setprop debug.oculus.gpuLevel \"\"", true);
                Log.Debug("Reset gpu level to default");
            }
            else if (int.TryParse(SelectedGpuLevel, out var gpuLevel))
            {
                ServiceContainer.ADBService.Device!.RunShellCommand(
                    $"setprop debug.oculus.gpuLevel {gpuLevel}", true);
                Log.Debug("Set gpu level: {GpuLevel}", gpuLevel);
            }
            if (string.IsNullOrEmpty(SelectedCpuLevel) || SelectedCpuLevel == "Default")
            {
                ServiceContainer.ADBService.Device!.RunShellCommand(
                    $"setprop debug.oculus.cpuLevel \"\"", true);
                Log.Debug("Reset cpu level to default");
            }
            else if (int.TryParse(SelectedCpuLevel, out var cpuLevel))
            {
                ServiceContainer.ADBService.Device!.RunShellCommand(
                    $"setprop debug.oculus.cpuLevel {cpuLevel}", true);
                Log.Debug("Set cpu level: {CpuLevel}", cpuLevel);
            }

            if (string.IsNullOrEmpty(ResolutionTextBoxText))
            {
                ServiceContainer.ADBService.Device!.RunShellCommand(
                    $"setprop debug.oculus.textureWidth \"\"", true);
                ServiceContainer.ADBService.Device!.RunShellCommand(
                    $"setprop debug.oculus.textureHeight \"\"", true);
                Log.Debug("Reset render resolution to default");
            }
            if (TryParseResolutionString(ResolutionTextBoxText, out var width, out var height))
            {
                ServiceContainer.ADBService.Device!.RunShellCommand($"setprop debug.oculus.textureWidth {width}", true);
                ServiceContainer.ADBService.Device!.RunShellCommand($"setprop debug.oculus.textureHeight {height}", true);
                Log.Debug("Set render resolution Width:{Width} Height:{Height}", width, height);
            }
        });
    }

    private static bool TryParseResolutionString(string? input, out int width, out int height)
    {
        if (input is null)
        {
            width = 0;
            height = 0;
            return false;
        }
        
        // This might not be very intuitive, idk
        /*if (input.Length == 4 && int.TryParse(input, out var value))
        {
            width = value;
            height = value;
            return true;
        }*/

        if (input.Contains('x'))
        {
            const string resolutionStringPattern = @"^(\d{4})x(\d{4})$";
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
    }

    private IObservable<Unit> MountStorageImpl()
    {
        return Observable.Start(() =>
        {
            if (!ServiceContainer.ADBService.ValidateDeviceConnection()) return;
            ServiceContainer.ADBService.Device!.RunShellCommand("svc usb setFunctions mtp true", true);
            Log.Information("Mounted device storage");
        });
    }
    private IObservable<Unit> LaunchHiddenSettingsImpl()
    {
        return Observable.Start(() =>
        {
            if (!ServiceContainer.ADBService.ValidateDeviceConnection()) return;
            ServiceContainer.ADBService.Device!.RunShellCommand("am start -a android.intent.action.VIEW -d com.oculus.tv -e uri com.android.settings/.DevelopmentSettings com.oculus.vrshell/.MainActivity", true);
            Log.Information("Launched hidden settings");
        });
    }
}