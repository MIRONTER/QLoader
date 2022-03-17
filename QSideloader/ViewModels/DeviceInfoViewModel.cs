using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Timers;
using AdvancedSharpAdbClient;
using Avalonia.Threading;
using QSideloader.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class DeviceInfoViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly ObservableAsPropertyHelper<bool> _isBusy;
    private Timer? _refreshTimer;
    private readonly AdbService _adbService;

    public DeviceInfoViewModel()
    {
        _adbService = ServiceContainer.AdbService;
        Activator = new ViewModelActivator();
        Refresh = ReactiveCommand.CreateFromObservable(RefreshImpl);
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Refresh.Execute().Subscribe();
        SetRefreshTimer(true);
        PropertyChanged += OnPropertyChanged;
        this.WhenActivated(disposables =>
        {
            _adbService.DeviceOnline += OnDeviceOnline;
            _adbService.DeviceOffline += OnDeviceOffline;
            _adbService.PackageListChanged += OnPackageListChanged;
            _adbService.DeviceListChanged += OnDeviceListChanged;
            Disposable.Create(() =>
            {
                _adbService.DeviceOnline -= OnDeviceOnline;
                _adbService.DeviceOffline -= OnDeviceOffline;
                _adbService.PackageListChanged -= OnPackageListChanged;
                _adbService.DeviceListChanged -= OnDeviceListChanged;
            }).DisposeWith(disposables);
        });
    }

    private ReactiveCommand<Unit, Unit> Refresh { get; }

    public bool IsBusy => _isBusy.Value;

    [Reactive] public string? FriendlyName { get; private set; }
    [Reactive] public bool IsQuest1 { get; private set; }
    [Reactive] public bool IsQuest2 { get; private set; }
    [Reactive] public float SpaceUsed { get; private set; }
    [Reactive] public float SpaceFree { get; private set; }
    [Reactive] public float BatteryLevel { get; private set; }
    [Reactive] public bool IsDeviceConnected { get; set; }
    [Reactive] public AdbService.AdbDevice? CurrentDevice { get; set; }
    [Reactive] public List<AdbService.AdbDevice> DeviceList { get; set; } = new();
    public ViewModelActivator Activator { get; }

    private void OnDeviceOnline(object? sender, EventArgs e)
    {
        IsDeviceConnected = true;
        Refresh.Execute().Subscribe();
        SetRefreshTimer(true);
    }

    private void OnDeviceOffline(object? sender, EventArgs e)
    {
        IsDeviceConnected = false;
        CurrentDevice = null;
        SetRefreshTimer(false);
    }

    private void OnPackageListChanged(object? sender, EventArgs e)
    {
        Refresh.Execute().Subscribe();
    }

    private IObservable<Unit> RefreshImpl()
    {
        return Observable.Start(() =>
        {
            RefreshDeviceInfo();
            RefreshProps();
        });
    }

    private void RefreshDeviceInfo()
    {
        if (!_adbService.ValidateDeviceConnection())
        {
            Log.Warning("RefreshDeviceInfo: no device connection!");
            IsDeviceConnected = false;
            CurrentDevice = null;
            SetRefreshTimer(false);
            return;
        }

        IsDeviceConnected = true;
        _adbService.Device!.RefreshInfo();
        _adbService.Device.RefreshInstalledPackages();
    }

    private void RefreshProps()
    {
        var device = _adbService.Device;
        RefreshSelectedDevice();
        if (device is null) return;
        SpaceUsed = device.SpaceUsed;
        SpaceFree = device.SpaceFree;
        BatteryLevel = device.BatteryLevel;
        FriendlyName = device.FriendlyName;
        IsQuest1 = device.Product == "monterey";
        IsQuest2 = device.Product == "hollywood";
    }

    private void OnDeviceListChanged(object? sender, EventArgs e)
    {
        RefreshSelectedDevice();
    }
    
    private void RefreshSelectedDevice()
    {
        DeviceList = _adbService.DeviceList.ToList();
        CurrentDevice = DeviceList.FirstOrDefault(x => _adbService.Device?.Serial == x.Serial);
    }

    private void SetRefreshTimer(bool start)
    {
        if (start)
        {
            if (_refreshTimer is null)
            {
                _refreshTimer = new Timer(180000);
                _refreshTimer.Elapsed += (_, _) => Refresh.Execute().Subscribe();
                _refreshTimer.AutoReset = true;
            }

            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer?.Stop();
        }
    }
    
    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "CurrentDevice" && CurrentDevice is not null && CurrentDevice.Serial != _adbService.Device?.Serial)
        {
            _adbService.TrySwitchDevice(CurrentDevice);
            RefreshSelectedDevice();
        }
    }
}