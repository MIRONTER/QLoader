using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using AdvancedSharpAdbClient;
using Avalonia.Threading;
using DynamicData;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class GameDonationViewModel: ViewModelBase, IActivatableViewModel
{
    private static readonly SemaphoreSlim RefreshSemaphoreSlim = new(1, 1);
    private readonly AdbService _adbService;
    private readonly SideloaderSettingsViewModel _sideloaderSettings;
    private readonly ReadOnlyObservableCollection<InstalledApp> _installedApps;
    private readonly SourceCache<InstalledApp, string> _installedAppsSourceCache = new(x => x.Name);
    private readonly ObservableAsPropertyHelper<bool> _isBusy;
    public GameDonationViewModel()
    {
        Activator = new ViewModelActivator();
        _adbService = AdbService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        Activator = new ViewModelActivator();
        Refresh = ReactiveCommand.CreateFromObservable(() => RefreshImpl());
        ManualRefresh = ReactiveCommand.CreateFromObservable(() => RefreshImpl(true));
        var isBusyCombined = Refresh.IsExecuting
            .CombineLatest(ManualRefresh.IsExecuting, (x, y) => x || y);
        isBusyCombined.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Donate = ReactiveCommand.CreateFromObservable(DonateImpl);
        DonateAll = ReactiveCommand.CreateFromObservable(DonateAllImpl);
        Ignore = ReactiveCommand.CreateFromObservable(IgnoreImpl);
        var cacheListBind = _installedAppsSourceCache.Connect()
            .RefCount()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Filter(x => !x.IsHiddenFromDonation)
            .SortBy(x => x.Name)
            .Bind(out _installedApps)
            .DisposeMany();
        this.WhenActivated(disposables =>
        {
            cacheListBind.Subscribe().DisposeWith(disposables);
            _adbService.WhenDeviceChanged.Subscribe(OnDeviceChanged).DisposeWith(disposables);
            _adbService.WhenPackageListChanged.Subscribe(_ => Refresh.Execute().Subscribe()).DisposeWith(disposables);
            Globals.MainWindowViewModel!.WhenGameDonated.Subscribe(_ => Refresh.Execute().Subscribe()).DisposeWith(disposables);
            IsDeviceConnected = _adbService.CheckDeviceConnectionSimple();
            Refresh.Execute().Subscribe();
        });
    }
    
    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> ManualRefresh { get; }
    public ReactiveCommand<Unit, Unit> Donate { get; }
    public ReactiveCommand<Unit, Unit> DonateAll { get; }
    public ReactiveCommand<Unit, Unit> Ignore { get; }
    public ReadOnlyObservableCollection<InstalledApp> InstalledApps => _installedApps;
    public bool IsBusy => _isBusy.Value;
    [Reactive] public bool IsDeviceConnected { get; private set; }

    public ViewModelActivator Activator { get; }
    
    private IObservable<Unit> RefreshImpl(bool rescan = false)
    {
        return Observable.Start(() =>
        {
            // Check whether refresh is already running
            if (RefreshSemaphoreSlim.CurrentCount == 0) return;
            RefreshSemaphoreSlim.Wait();
            try
            {
                RefreshInstalledApps(rescan);
                Globals.MainWindowViewModel!.RefreshGameDonationBadge();
            }
            finally
            {
                RefreshSemaphoreSlim.Release();
            }
        });
    }
    
    private IObservable<Unit> DonateImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnectionSimple())
            {
                Log.Warning("GameDonationViewModel.DonateImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            var selectedApps = _installedAppsSourceCache.Items.Where(app => app.IsSelected).ToList();
            if (selectedApps.Count == 0)
            {
                Log.Warning("No apps selected for donation");
                return;
            }
            foreach (var app in selectedApps)
            {
                app.IsSelected = false;
                Globals.MainWindowViewModel!.EnqueueTask(app, TaskType.PullAndUpload);
                Log.Information("Queued for donation: {ReleaseName}", app.Name);
            }
        });
    }

    private IObservable<Unit> DonateAllImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnectionSimple())
            {
                Log.Warning("GameDonationViewModel.DonateAllImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            Log.Information("Donating all eligible apps");
            var runningDonations = Globals.MainWindowViewModel!.GetTaskList()
                .Where(x => x.TaskType == TaskType.PullAndUpload && !x.IsFinished).ToList();
            if (InstalledApps.Count == 0)
            {
                Log.Warning("No apps to donate!");
                return;
            }

            foreach (var app in InstalledApps)
            {
                if (runningDonations.Any(x => x.PackageName == app.PackageName))
                {
                    Log.Debug("Skipping {Name} because it is already being donated", app.Name);
                    continue;
                }
                
                app.IsSelected = false;
                Globals.MainWindowViewModel.EnqueueTask(app, TaskType.PullAndUpload);
                Log.Information("Queued for donation: {Name}", app.Name);
            }
        });
    }
    
    private IObservable<Unit> IgnoreImpl()
    {
        return Observable.Start(() =>
        {
            var selectedApps = _installedAppsSourceCache.Items.Where(app => app.IsSelected).ToList();
            if (selectedApps.Count == 0)
            {
                Log.Warning("No apps selected to add to ignore list");
                return;
            }
            foreach (var app in selectedApps)
            {
                app.IsSelected = false;
                _sideloaderSettings.IgnoredDonationPackages.Add(app.PackageName);
            }
            _adbService.Device?.RefreshInstalledApps();
            RefreshInstalledApps(false);
            Globals.MainWindowViewModel!.RefreshGameDonationBadge();
        });
    }

    private void OnDeviceChanged(AdbService.AdbDevice device)
    {
        switch (device.State)
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
    }

    private void OnDeviceOffline()
    {
        IsDeviceConnected = false;
        Dispatcher.UIThread.InvokeAsync(_installedAppsSourceCache.Clear);
    }

    private void RefreshInstalledApps(bool rescan)
    {
        if (!_adbService.CheckDeviceConnectionSimple())
        {
            Log.Warning("GameDonationViewModel.RefreshInstalledApps: no device connection!");
            OnDeviceOffline();
            return;
        }
        
        IsDeviceConnected = true;
        if (rescan)
        {
            _adbService.Device?.RefreshInstalledApps();
        }
        while (_adbService.Device!.IsRefreshingInstalledGames)
        {
            Thread.Sleep(100);
            if (_adbService.Device is null)
                return;
        }
        _installedAppsSourceCache.Edit(innerCache =>
        {
            innerCache.Clear();
            innerCache.AddOrUpdate(_adbService.Device!.InstalledApps);
        });

    }
}