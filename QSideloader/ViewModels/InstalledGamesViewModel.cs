using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.Services;
using QSideloader.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class InstalledGamesViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly ObservableAsPropertyHelper<bool> _isBusy;
    private List<InstalledGame>? _installedGames;
    private readonly AdbService _adbService;
    private readonly DownloaderService _downloaderService;

    public InstalledGamesViewModel()
    {
        _adbService = ServiceContainer.AdbService;
        _downloaderService = ServiceContainer.DownloaderService;
        Activator = new ViewModelActivator();
        Refresh = ReactiveCommand.CreateFromObservable(RefreshImpl);
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Update = ReactiveCommand.CreateFromObservable(UpdateImpl);
        Uninstall = ReactiveCommand.CreateFromObservable(UninstallImpl);
        // Placing refresh outside of WhenActivated may cause the data to be outdated
        // But I think it's better than refreshing every time (refresh takes 1-1.5s)
        Refresh.Execute().Subscribe();
        this.WhenActivated(disposables =>
        {
            _adbService.DeviceOnline += OnDeviceOnline;
            _adbService.DeviceOffline += OnDeviceOffline;
            _adbService.PackageListChanged += OnPackageListChanged;
            
            Disposable
                .Create(() =>
                {
                    _adbService.DeviceOnline -= OnDeviceOnline;
                    _adbService.DeviceOffline -= OnDeviceOffline;
                    _adbService.PackageListChanged -= OnPackageListChanged;
                })
                .DisposeWith(disposables);
        });
    }

    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> Update { get; }
    public ReactiveCommand<Unit, Unit> Uninstall { get; }
    [Reactive] public ObservableCollection<InstalledGame> InstalledGames { get; set; } = new();
    public bool IsBusy => _isBusy.Value;
    [Reactive] public bool MultiSelectEnabled { get; set; } = true;
    public ViewModelActivator Activator { get; }

    private IObservable<Unit> RefreshImpl()
    {
        return Observable.Start(() =>
        {
            Log.Information("Refreshing list of installed games");
            RefreshInstalledGames();
            RefreshProps();
        });
    }

    // TODO: handle update failures (incompatible signatures, wrong version, etc.)
    private IObservable<Unit> UpdateImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.ValidateDeviceConnection())
            {
                Log.Warning("UpdateImpl: no device connection!");
                return;
            }

            var selectedGames = InstalledGames.Where(game => game.IsSelected);
            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var taskView = new TaskView();
                    taskView.ViewModel!.Game = game;
                    taskView.ViewModel!.PerformTask.Execute().Subscribe();
                    Globals.MainWindowViewModel!.TaskList.Add(taskView);
                });
                Log.Information("Queued for update: {ReleaseName}", game.ReleaseName);
            }
        });
    }

    private IObservable<Unit> UninstallImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.ValidateDeviceConnection())
            {
                Log.Warning("UninstallImpl: no device connection!");
                return;
            }

            var selectedGames = InstalledGames.Where(game => game.IsSelected);
            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                _adbService.Device!.UninstallGame(game);
                Log.Information("Uninstalled game: {GameName}", game.GameName);
            }
        });
    }

    private void OnDeviceOffline(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(InstalledGames.Clear);
    }

    private void OnDeviceOnline(object? sender, EventArgs e)
    {
        Refresh.Execute().Subscribe();
    }
    
    private void OnPackageListChanged(object? sender, EventArgs e)
    {
        Refresh.Execute().Subscribe();
    }

    private void RefreshInstalledGames()
    {
        if (!_adbService.ValidateDeviceConnection())
        {
            Log.Warning("RefreshInstalledGames: no device connection!");
            _installedGames = null;
            return;
        }

        _downloaderService.EnsureGameListAvailableAsync().GetAwaiter().GetResult();
        _adbService.Device!.RefreshInstalledPackages();
        _installedGames = _adbService.Device.GetInstalledGames();
    }

    private void RefreshProps()
    {
        Dispatcher.UIThread.InvokeAsync(PopulateInstalledGames);
    }

    private void PopulateInstalledGames()
    {
        if (_installedGames is null)
        {
            InstalledGames.Clear();
            return;
        }

        var toAdd = _installedGames.Except(InstalledGames).ToList();
        var toRemove = InstalledGames.Except(toAdd).ToList();
        foreach (var addedGame in toAdd)
            InstalledGames.Add(addedGame);
        foreach (var removedGame in toRemove)
            InstalledGames.Remove(removedGame);
    }
}