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

public class InstalledGamesViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly ObservableAsPropertyHelper<bool> _isBusy;
    private readonly AdbService _adbService;
    private readonly DownloaderService _downloaderService;
    private readonly SourceCache<InstalledGame, string> _installedGamesSourceCache = new(x => x.ReleaseName!);
    private readonly ReadOnlyObservableCollection<InstalledGame> _installedGames;
    private static readonly SemaphoreSlim RefreshSemaphoreSlim = new(1, 1);

    public InstalledGamesViewModel()
    {
        _adbService = ServiceContainer.AdbService;
        _downloaderService = ServiceContainer.DownloaderService;
        Activator = new ViewModelActivator();
        Refresh = ReactiveCommand.CreateFromObservable(RefreshImpl);
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Update = ReactiveCommand.CreateFromObservable(UpdateImpl);
        Uninstall = ReactiveCommand.CreateFromObservable(UninstallImpl);
        var cacheListBind = _installedGamesSourceCache.Connect()
            .RefCount()
            .SortBy(x => x.ReleaseName!)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _installedGames)
            .DisposeMany();
        // Placing refresh outside of WhenActivated may cause the data to be outdated
        // But I think it's better than refreshing every time (refresh takes 1-1.5s)
        Refresh.Execute().Subscribe();
        this.WhenActivated(disposables =>
        {
            cacheListBind.Subscribe().DisposeWith(disposables);
            _adbService.WhenDeviceChanged.Subscribe(OnDeviceChanged).DisposeWith(disposables);
            _adbService.WhenPackageListChanged.Subscribe(_ => OnPackageListChanged()).DisposeWith(disposables);
            IsDeviceConnected = _adbService.CheckDeviceConnectionSimple();
        });
    }

    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> Update { get; }
    public ReactiveCommand<Unit, Unit> Uninstall { get; }
    public ReadOnlyObservableCollection<InstalledGame> InstalledGames => _installedGames;
    public bool IsBusy => _isBusy.Value;
    [Reactive] public bool IsDeviceConnected { get; private set; }
    [Reactive] public bool MultiSelectEnabled { get; set; } = true;
    public ViewModelActivator Activator { get; }

    private IObservable<Unit> RefreshImpl()
    {
        return Observable.Start(() =>
        {
            // Check whether refresh is already running
            if (RefreshSemaphoreSlim.CurrentCount == 0) return;
            RefreshSemaphoreSlim.Wait();
            try
            {
                Log.Information("Refreshing list of installed games");
                RefreshInstalledGames();
            }
            finally
            {
                RefreshSemaphoreSlim.Release();
            }
        });
    }

    // TODO: handle update failures (incompatible signatures, wrong version, etc.)
    private IObservable<Unit> UpdateImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnection())
            {
                Log.Warning("InstalledGamesViewModel.UpdateImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            var selectedGames = _installedGamesSourceCache.Items.Where(game => game.IsSelected).ToList();
            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                Globals.MainWindowViewModel!.EnqueueTask(game, TaskType.DownloadAndInstall);
                Log.Information("Queued for update: {ReleaseName}", game.ReleaseName);
            }
        });
    }

    private IObservable<Unit> UninstallImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnection())
            {
                Log.Warning("InstalledGamesViewModel.UninstallImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            var selectedGames = _installedGamesSourceCache.Items.Where(game => game.IsSelected).ToList();
            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                Globals.MainWindowViewModel!.EnqueueTask(game, TaskType.BackupAndUninstall);
            }
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
        Dispatcher.UIThread.InvokeAsync(_installedGamesSourceCache.Clear);
    }
    private void OnPackageListChanged()
    {
        Refresh.Execute().Subscribe();
    }

    private void RefreshInstalledGames()
    {
        if (!_adbService.CheckDeviceConnection())
        {
            Log.Warning("InstalledGamesViewModel.RefreshInstalledGames: no device connection!");
            OnDeviceOffline();
            return;
        }

        IsDeviceConnected = true;
        _downloaderService.EnsureGameListAvailableAsync().GetAwaiter().GetResult();
        _adbService.Device!.RefreshInstalledPackages();
        var installedGames = _adbService.Device.GetInstalledGames();
        _installedGamesSourceCache.Edit(innerCache =>
        {
            innerCache.Clear();
            innerCache.AddOrUpdate(installedGames);
        });
    }
}