using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
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
            _adbService.WhenDeviceChanged.Subscribe(OnDeviceChange).DisposeWith(disposables);
            _adbService.WhenPackageListChanged.Subscribe(_ => OnPackageListChanged()).DisposeWith(disposables);
        });
    }

    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> Update { get; }
    public ReactiveCommand<Unit, Unit> Uninstall { get; }
    public ReadOnlyObservableCollection<InstalledGame> InstalledGames => _installedGames;
    public bool IsBusy => _isBusy.Value;
    [Reactive] public bool MultiSelectEnabled { get; set; } = true;
    public ViewModelActivator Activator { get; }

    private IObservable<Unit> RefreshImpl()
    {
        return Observable.Start(() =>
        {
            Log.Information("Refreshing list of installed games");
            RefreshInstalledGames();
        });
    }

    // TODO: handle update failures (incompatible signatures, wrong version, etc.)
    private IObservable<Unit> UpdateImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnection())
            {
                Log.Warning("UpdateImpl: no device connection!");
                return;
            }

            var selectedGames = InstalledGames.Where(game => game.IsSelected);
            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                Dispatcher.UIThread.InvokeAsync(() => { Globals.MainWindowViewModel!.QueueForInstall(game); });
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
                Log.Warning("UninstallImpl: no device connection!");
                return;
            }

            var selectedGames = InstalledGames.Where(game => game.IsSelected);
            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                _adbService.Device!.BackupGame(game);
                _adbService.Device!.UninstallGame(game);
                Log.Information("Uninstalled game: {GameName}", game.GameName);
            }
        });
    }

    private void OnDeviceChange(AdbService.AdbDevice device)
    {
        switch (device.State)
        {
            case DeviceState.Online:
                Refresh.Execute().Subscribe();
                break;
            case DeviceState.Offline:
                Dispatcher.UIThread.InvokeAsync(_installedGamesSourceCache.Clear);
                break;
        }
    }

    private void OnPackageListChanged()
    {
        Refresh.Execute().Subscribe();
    }

    private void RefreshInstalledGames()
    {
        if (!_adbService.CheckDeviceConnection())
        {
            Log.Warning("RefreshInstalledGames: no device connection!");
            _installedGamesSourceCache.Clear();
            return;
        }

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