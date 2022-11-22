using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using AdvancedSharpAdbClient;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using DynamicData;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Services;
using QSideloader.Utilities;
using QSideloader.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class InstalledGamesViewModel : ViewModelBase, IActivatableViewModel
{
    private static readonly SemaphoreSlim RefreshSemaphoreSlim = new(1, 1);
    private readonly AdbService _adbService;
    private readonly ReadOnlyObservableCollection<InstalledGame> _installedGames;
    private readonly SourceCache<InstalledGame, string> _installedGamesSourceCache = new(x => x.ReleaseName!);
    private readonly ObservableAsPropertyHelper<bool> _isBusy;

    public InstalledGamesViewModel()
    {
        _adbService = AdbService.Instance;
        Activator = new ViewModelActivator();
        Refresh = ReactiveCommand.CreateFromObservable<bool, Unit>(RefreshImpl);
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Update = ReactiveCommand.CreateFromObservable(UpdateImpl);
        UpdateAll = ReactiveCommand.CreateFromObservable(UpdateAllImpl);
        Uninstall = ReactiveCommand.CreateFromObservable(UninstallImpl);
        Backup = ReactiveCommand.CreateFromObservable(BackupImpl);
        var cacheListBind = _installedGamesSourceCache.Connect()
            .RefCount()
            .SortBy(x => x.ReleaseName!)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _installedGames)
            .DisposeMany();
        this.WhenActivated(disposables =>
        {
            cacheListBind.Subscribe().DisposeWith(disposables);
            _adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged).DisposeWith(disposables);
            _adbService.WhenPackageListChanged.Subscribe(_ => Refresh.Execute().Subscribe()).DisposeWith(disposables);
            IsDeviceConnected = _adbService.CheckDeviceConnectionSimple();
            Refresh.Execute().Subscribe();
        });
    }

    public ReactiveCommand<bool, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> Update { get; }
    public ReactiveCommand<Unit, Unit> UpdateAll { get; }
    public ReactiveCommand<Unit, Unit> Uninstall { get; }
    public ReactiveCommand<Unit, Unit> Backup { get; }
    public ReadOnlyObservableCollection<InstalledGame> InstalledGames => _installedGames;
    public bool IsBusy => _isBusy.Value;
    [Reactive] public bool IsDeviceConnected { get; private set; }
    [Reactive] public bool MultiSelectEnabled { get; set; } = true;
    [Reactive] public bool ManualBackupAppFiles { get; set; } = true;
    [Reactive] public bool ManualBackupData { get; set; } = true;
    [Reactive] public bool SkipAutoBackup { get; set; }
    public ViewModelActivator Activator { get; }

    private IObservable<Unit> RefreshImpl(bool rescanGames = false)
    {
        return Observable.Start(() =>
        {
            // Check whether refresh is already running
            if (RefreshSemaphoreSlim.CurrentCount == 0) return;
            RefreshSemaphoreSlim.Wait();
            try
            {
                RefreshInstalledGames(rescanGames);
            }
            finally
            {
                RefreshSemaphoreSlim.Release();
            }
        });
    }

    private IObservable<Unit> UpdateImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnectionSimple())
            {
                Log.Warning("InstalledGamesViewModel.UpdateImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            var selectedGames = _installedGamesSourceCache.Items.Where(game => game.IsSelected).ToList();
            if (selectedGames.Count == 0)
            {
                Log.Information("No games selected for update");
                Globals.ShowNotification(Resources.Update, Resources.NoGamesSelected, NotificationType.Information,
                    TimeSpan.FromSeconds(2));
                return;
            }

            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.DownloadAndInstall, Game = game});
                Log.Information("Queued for update: {ReleaseName}", game.ReleaseName);
            }
        });
    }

    private IObservable<Unit> UpdateAllImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnectionSimple())
            {
                Log.Warning("InstalledGamesViewModel.UpdateAllImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            Log.Information("Running auto-update");
            var runningInstalls = new List<TaskView>();
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                runningInstalls = Globals.MainWindowViewModel!.GetTaskList()
                    .Where(x => x.TaskType is TaskType.DownloadAndInstall or TaskType.InstallOnly && !x.IsFinished)
                    .ToList();
            }).Wait();
            // Find package name duplicates to avoid installing the wrong release
            var ambiguousReleases = _installedGamesSourceCache.Items.GroupBy(x => x.PackageName)
                .Where(x => x.Skip(1).Any()).SelectMany(x => x).ToList();
            Log.Information("Found {AmbiguousReleasesCount} ambiguous releases, which will be ignored",
                ambiguousReleases.Count);
            var selectedGames = _installedGamesSourceCache.Items
                .Where(game => game.AvailableVersionCode > game.InstalledVersionCode).ToList();
            var skippedCount = selectedGames.Count;
            selectedGames.RemoveAll(x => ambiguousReleases.Contains(x));
            var alreadyUpdating = selectedGames.Where(x => runningInstalls.Any(y => y.PackageName == x.PackageName))
                .ToList();
            if (alreadyUpdating.Count > 0)
            {
                selectedGames.RemoveAll(x => alreadyUpdating.Contains(x));
                Log.Information("Skipped {SkippedCount} games that are already being updated", alreadyUpdating.Count);
            }

            skippedCount -= selectedGames.Count;

            if (selectedGames.Count == 0)
            {
                if (skippedCount == 0)
                {
                    Log.Information("No games to update");
                    Globals.ShowNotification(Resources.Update, Resources.NoGamesToUpdate, NotificationType.Information,
                        TimeSpan.FromSeconds(2));
                }
                else
                {
                    Log.Information("No games to update ({SkippedCount} skipped)", skippedCount);
                    Globals.ShowNotification(Resources.Update,
                        string.Format(Resources.NoGamesToUpdateSkipped, skippedCount),
                        NotificationType.Information, TimeSpan.FromSeconds(2));
                }

                return;
            }

            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.DownloadAndInstall, Game = game});
                Log.Information("Queued for update: {ReleaseName}", game.ReleaseName);
            }
        });
    }

    private IObservable<Unit> UninstallImpl()
    {
        return Observable.Start(() =>
        {
            var skipBackup = SkipAutoBackup;
            SkipAutoBackup = false;
            if (!_adbService.CheckDeviceConnectionSimple())
            {
                Log.Warning("InstalledGamesViewModel.UninstallImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            var selectedGames = _installedGamesSourceCache.Items.Where(game => game.IsSelected).ToList();
            if (selectedGames.Count == 0)
            {
                Log.Information("No games selected for uninstall");
                Globals.ShowNotification(Resources.Uninstall, Resources.NoGamesSelected, NotificationType.Information,
                    TimeSpan.FromSeconds(2));
                return;
            }

            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                if (skipBackup)
                {
                    Globals.MainWindowViewModel!.AddTask(new TaskOptions
                        {Type = TaskType.Uninstall, Game = game});
                }
                else
                {
                    Globals.MainWindowViewModel!.AddTask(new TaskOptions
                    {
                        Type = TaskType.BackupAndUninstall, Game = game,
                        BackupOptions = new BackupOptions {BackupData = true, BackupApk = false, BackupObb = false}
                    });
                }
            }
        });
    }

    private IObservable<Unit> BackupImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnectionSimple())
            {
                Log.Warning("InstalledGamesViewModel.BackupImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            var selectedGames = _installedGamesSourceCache.Items.Where(game => game.IsSelected).ToList();
            if (selectedGames.Count == 0)
            {
                Log.Information("No games selected for backup");
                Globals.ShowNotification(Resources.Backup, Resources.NoGamesSelected, NotificationType.Information,
                    TimeSpan.FromSeconds(2));
                return;
            }

            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                var backupOptions = new BackupOptions
                {
                    BackupApk = ManualBackupAppFiles,
                    BackupObb = ManualBackupAppFiles,
                    BackupData = ManualBackupData
                };
                Globals.MainWindowViewModel!.AddTask(new TaskOptions
                    {Type = TaskType.Backup, Game = game, BackupOptions = backupOptions});
            }
        });
    }

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
    }

    private void OnDeviceOffline()
    {
        IsDeviceConnected = false;
        Dispatcher.UIThread.InvokeAsync(_installedGamesSourceCache.Clear);
    }

    private void RefreshInstalledGames(bool rescanGames)
    {
        if ((rescanGames && !_adbService.CheckDeviceConnection()) ||
            (!rescanGames && !_adbService.CheckDeviceConnectionSimple()))
        {
            Log.Warning("InstalledGamesViewModel.RefreshInstalledGames: no device connection!");
            OnDeviceOffline();
            return;
        }

        IsDeviceConnected = true;
        if (rescanGames)
            _adbService.Device!.RefreshInstalledGames();
        while (_adbService.Device!.IsRefreshingInstalledGames)
        {
            Thread.Sleep(100);
            if (_adbService.Device is null)
                return;
        }

        _installedGamesSourceCache.Edit(innerCache =>
        {
            innerCache.AddOrUpdate(_adbService.Device!.InstalledGames);
            innerCache.Remove(_installedGamesSourceCache.Items.Except(_adbService.Device!.InstalledGames));
        });
    }
}