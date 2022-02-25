using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Timers;
using QSideloader.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class DeviceInfoViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly ObservableAsPropertyHelper<bool> _isBusy;
    private Timer? _refreshTimer;

    public DeviceInfoViewModel()
    {
        Activator = new ViewModelActivator();
        Refresh = ReactiveCommand.CreateFromObservable(RefreshImpl);
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Refresh.Execute().Subscribe();
        SetRefreshTimer(true);
        this.WhenActivated(disposables =>
        {
            HandleActivation(disposables);
            Disposable.Create(HandleDispose).DisposeWith(disposables);
        });
    }

    public ReactiveCommand<Unit, Unit> Refresh { get; }

    public bool IsBusy => _isBusy.Value;

    [Reactive] public string? FriendlyName { get; private set; }
    [Reactive] public float SpaceUsed { get; private set; }
    [Reactive] public float SpaceFree { get; private set; }
    [Reactive] public float BatteryLevel { get; private set; }
    [Reactive] public bool IsDeviceConnected { get; set; }

    [Reactive] public string DownloaderStatus { get; set; } = "Loading";
    [Reactive] public string ADBStatus { get; set; } = "Starting";
    public ViewModelActivator Activator { get; }

    private void HandleActivation(CompositeDisposable disposables)
    {
        /*var deviceOnline = Observable.FromEventPattern(
            handler => ServiceContainer.ADBService.DeviceOnline += handler,
            handler => ServiceContainer.ADBService.DeviceOnline -= handler);
        var deviceOffline = Observable.FromEventPattern(
            handler => ServiceContainer.ADBService.DeviceOffline += handler,
            handler => ServiceContainer.ADBService.DeviceOffline -= handler);
        var adbStatusChanged = Observable.FromEventPattern(
            handler => ServiceContainer.ADBService.StatusChanged += handler,
            handler => ServiceContainer.ADBService.StatusChanged -= handler);
        var downloaderStatusChanged = Observable.FromEventPattern(
            handler => ServiceContainer.DownloaderService.StatusChanged += handler,
            handler => ServiceContainer.DownloaderService.StatusChanged -= handler);
        deviceOnline.Subscribe(_ => OnDeviceOnline()).DisposeWith(disposables);
        deviceOffline.Subscribe(_ => OnDeviceOffline()).DisposeWith(disposables);
        adbStatusChanged.Subscribe(_ => ADBStatus = ServiceContainer.ADBService.Status)
            .DisposeWith(disposables);
        downloaderStatusChanged.Subscribe(_ => DownloaderStatus = ServiceContainer.DownloaderService.Status)
            .DisposeWith(disposables);*/
        ServiceContainer.ADBService.DeviceOnline += OnDeviceOnline;
        ServiceContainer.ADBService.DeviceOffline += OnDeviceOffline;
    }

    private void HandleDispose()
    {
        ServiceContainer.ADBService.DeviceOnline -= OnDeviceOnline;
        ServiceContainer.ADBService.DeviceOffline -= OnDeviceOffline;
    }

    private void OnDeviceOnline(object? sender, EventArgs e)
    {
        Refresh.Execute().Subscribe();
        SetRefreshTimer(true);
    }

    private void OnDeviceOffline(object? sender, EventArgs e)
    {
        IsDeviceConnected = false;
        SetRefreshTimer(false);
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
        if (!ServiceContainer.ADBService.ValidateDeviceConnection())
        {
            Log.Warning("RefreshDeviceInfo: no device connection!");
            IsDeviceConnected = false;
            SetRefreshTimer(false);
            return;
        }

        IsDeviceConnected = true;
        ServiceContainer.ADBService.Device!.RefreshInfo();
        ServiceContainer.ADBService.Device.RefreshInstalledPackages();
    }

    private void RefreshProps()
    {
        var device = ServiceContainer.ADBService.Device;
        if (device is null) return;
        SpaceUsed = device.SpaceUsed;
        SpaceFree = device.SpaceFree;
        BatteryLevel = device.BatteryLevel;
        FriendlyName = device.FriendlyName;
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