using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using QSideloader.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;
using Timer = System.Timers.Timer;

namespace QSideloader.ViewModels;

public class DeviceInfoViewModel : ViewModelBase, IActivatableViewModel
{
    private static readonly SemaphoreSlim RefreshSemaphoreSlim = new(1, 1);
    private readonly AdbService _adbService;
    private readonly ObservableAsPropertyHelper<bool> _isBusy;
    private Timer? _refreshTimer;

    public DeviceInfoViewModel()
    {
        _adbService = AdbService.Instance;
        Activator = new ViewModelActivator();
        Refresh = ReactiveCommand.CreateFromObservable(RefreshImpl);
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        EnableWirelessAdb = ReactiveCommand.CreateFromTask(EnableWirelessAdbImpl);
        Refresh.Execute().Subscribe();
        this.WhenActivated(disposables =>
        {
            _adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged).DisposeWith(disposables);
            _adbService.WhenPackageListChanged.Subscribe(_ => OnPackageListChanged()).DisposeWith(disposables);
            _adbService.WhenDeviceListChanged.Subscribe(OnDeviceListChanged).DisposeWith(disposables);
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
    [Reactive] public string? TrueSerial { get; set; }
    [Reactive] public ObservableCollection<AdbService.AdbDevice> DeviceList { get; set; } = new();
    public ViewModelActivator Activator { get; }

    private void OnDeviceStateChanged(DeviceState state)
    {
        switch (state)
        {
            case DeviceState.Online:
                OnDeviceOnline();
                break;
            case DeviceState.Offline:
                OnDeviceOffline();
                break;
        }
    }

    private void OnDeviceOnline()
    {
        IsDeviceConnected = true;
        Refresh.Execute().Subscribe();
        SetRefreshTimer(true);
    }

    private void OnDeviceOffline()
    {
        IsDeviceConnected = false;
        CurrentDevice = null;
        TrueSerial = null;
        SetRefreshTimer(false);
    }

    private void OnPackageListChanged()
    {
        Refresh.Execute().Subscribe();
    }

    private IObservable<Unit> RefreshImpl()
    {
        return Observable.Start(() =>
        {
            // Check whether refresh is already in running
            if (RefreshSemaphoreSlim.CurrentCount == 0) return;
            RefreshSemaphoreSlim.Wait();
            try
            {
                RefreshDeviceInfo();
                RefreshProps();
            }
            finally
            {
                RefreshSemaphoreSlim.Release();
            }
        });
    }

    private async Task EnableWirelessAdbImpl()
    {
        if (!_adbService.CheckDeviceConnection())
        {
            Log.Warning("DeviceInfoViewModel.EnableWirelessAdbImpl: no device connection!");
            OnDeviceOffline();
            return;
        }

        await _adbService.EnableWirelessAdbAsync(_adbService.Device!);
    }

    private void RefreshDeviceInfo()
    {
        if (!_adbService.CheckDeviceConnection())
        {
            Log.Warning("DeviceInfoViewModel.RefreshDeviceInfo: no device connection!");
            OnDeviceOffline();
            return;
        }

        IsDeviceConnected = true;
        SetRefreshTimer(true);
        IsDeviceWireless = _adbService.Device!.IsWireless;
        _adbService.Device.RefreshInfo();
    }

    private void RefreshProps()
    {
        var device = _adbService.Device;
        DeviceList = new ObservableCollection<AdbService.AdbDevice>(_adbService.DeviceList.ToList());
        RefreshDeviceSelection();
        if (device is null) return;
        SpaceUsed = device.SpaceUsed;
        SpaceFree = device.SpaceFree;
        BatteryLevel = device.BatteryLevel;
        FriendlyName = device.FriendlyName;
        IsQuest1 = device.Product is "monterey" or "vr_monterey";
        IsQuest2 = device.Product == "hollywood";
    }

    private void OnDeviceListChanged(IReadOnlyList<AdbService.AdbDevice> deviceList)
    {
        var toAdd = deviceList.Where(device => DeviceList.All(x => x.Serial != device.Serial)).ToList();
        var toRemove = DeviceList.Where(device => deviceList.All(x => x.Serial != device.Serial)).ToList();
        foreach (var device in toAdd)
            DeviceList.Add(device);
        foreach (var device in toRemove)
            DeviceList.Remove(device);
    }

    private void RefreshDeviceSelection()
    {
        CurrentDevice = DeviceList.FirstOrDefault(x => _adbService.Device?.Serial == x.Serial);
        TrueSerial = CurrentDevice?.TrueSerial;
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