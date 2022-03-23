using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Timers;
using AdvancedSharpAdbClient;
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
        EnableWirelessAdb = ReactiveCommand.CreateFromTask(EnableWirelessAdbImpl);
        Refresh.Execute().Subscribe();
        SetRefreshTimer(true);
        this.WhenActivated(disposables =>
        {
            _adbService.DeviceChange.Subscribe(OnDeviceChanged).DisposeWith(disposables);
            _adbService.PackageListChange.Subscribe(_ => OnPackageListChanged()).DisposeWith(disposables);
            _adbService.DeviceListChange.Subscribe(OnDeviceListChanged).DisposeWith(disposables);
            this.WhenAnyValue(x => x.CurrentDevice).Where(x => x is not null && x.Serial != _adbService.Device?.Serial)
                .DistinctUntilChanged()
                .Subscribe(x =>
                {
                    _adbService.TrySwitchDevice(x!);
                    RefreshDeviceSelection();
                }).DisposeWith(disposables);
        });
    }

    private ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> EnableWirelessAdb { get; }

    public bool IsBusy => _isBusy.Value;

    [Reactive] public string? FriendlyName { get; private set; }
    [Reactive] public bool IsQuest1 { get; private set; }
    [Reactive] public bool IsQuest2 { get; private set; }
    [Reactive] public float SpaceUsed { get; private set; }
    [Reactive] public float SpaceFree { get; private set; }
    [Reactive] public float BatteryLevel { get; private set; }
    [Reactive] public bool IsDeviceConnected { get; set; }
    [Reactive] public bool IsDeviceWireless { get; set; }
    [Reactive] public AdbService.AdbDevice? CurrentDevice { get; set; }
    [Reactive] public List<AdbService.AdbDevice> DeviceList { get; set; } = new();
    public ViewModelActivator Activator { get; }

    private void OnDeviceChanged(AdbService.AdbDevice device)
    {
        switch (device.State)
        {
            case DeviceState.Online:
                IsDeviceConnected = true;
                Refresh.Execute().Subscribe();
                SetRefreshTimer(true);
                break;
            case DeviceState.Offline:
                IsDeviceConnected = false;
                CurrentDevice = null;
                SetRefreshTimer(false);
                break;
        }
    }

    private void OnPackageListChanged()
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
    
    private async Task EnableWirelessAdbImpl()
    {
        if (!_adbService.CheckDeviceConnection())
        {
            Log.Warning("EnableWirelessAdbImpl: no device connection!");
            return;
        }
        await _adbService.EnableWirelessAdbAsync(_adbService.Device!);
    }

    private void RefreshDeviceInfo()
    {
        if (!_adbService.CheckDeviceConnection())
        {
            Log.Warning("RefreshDeviceInfo: no device connection!");
            IsDeviceConnected = false;
            SetRefreshTimer(false);
            return;
        }

        IsDeviceConnected = true;
        IsDeviceWireless = _adbService.Device!.IsWireless;
        _adbService.Device.RefreshInfo();
    }

    private void RefreshProps()
    {
        var device = _adbService.Device;
        DeviceList = _adbService.DeviceList.ToList();
        RefreshDeviceSelection();
        if (device is null) return;
        SpaceUsed = device.SpaceUsed;
        SpaceFree = device.SpaceFree;
        BatteryLevel = device.BatteryLevel;
        FriendlyName = device.FriendlyName;
        IsQuest1 = device.Product == "monterey";
        IsQuest2 = device.Product == "hollywood";
    }

    private void OnDeviceListChanged(IReadOnlyList<AdbService.AdbDevice> deviceList)
    {
        var toAdd = deviceList.Where(device => DeviceList.All(x => x.Serial != device.Serial));
        var toRemove = DeviceList.Where(device => deviceList.All(x => x.Serial != device.Serial));
        foreach (var device in toAdd)
            DeviceList.Add(device);
        foreach (var device in toRemove)
            DeviceList.Remove(device);
    }
    
    private void RefreshDeviceSelection()
    {
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
}