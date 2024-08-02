using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using AdvancedSharpAdbClient.Models;
using Avalonia.Controls.Notifications;
using DynamicData;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Services;
using QSideloader.Utilities;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class DownloadedGamesViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly AdbService _adbService;
    private readonly DownloaderService _downloaderService;
    private readonly ReadOnlyObservableCollection<DownloadedGame> _downloadedGames;
    private readonly SourceCache<DownloadedGame, string> _downloadedGamesSourceCache = new(x => x.ReleaseName!);
    private readonly ObservableAsPropertyHelper<bool> _isBusy;

    public DownloadedGamesViewModel()
    {
        Activator = new ViewModelActivator();
        _adbService = AdbService.Instance;
        _downloaderService = DownloaderService.Instance;
        Refresh = ReactiveCommand.CreateFromObservable(RefreshImpl);
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Install = ReactiveCommand.CreateFromObservable(InstallImpl);
        Delete = ReactiveCommand.CreateFromObservable(DeleteImpl);
        _downloadedGamesSourceCache.Connect()
            .RefCount()
            .ObserveOn(RxApp.MainThreadScheduler)
            .SortBy(x => x.ReleaseName!)
            .Bind(out _downloadedGames)
            .DisposeMany().Subscribe();
        this.WhenActivated(disposables =>
        {
            _adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged).DisposeWith(disposables);
            _downloaderService.WhenDownloadedGamesListChanged.Subscribe(_ => OnDownloadedGamesListChanged())
                .DisposeWith(disposables);
            Refresh.Execute().Subscribe();
        });
    }

    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> Install { get; }
    public ReactiveCommand<Unit, Unit> Delete { get; }
    public ReadOnlyObservableCollection<DownloadedGame> DownloadedGames => _downloadedGames;
    public bool IsBusy => _isBusy.Value;
    [Reactive] public bool IsDeviceConnected { get; private set; }

    public ViewModelActivator Activator { get; }
    
    private IObservable<Unit> RefreshImpl()
    {
        return Observable.StartAsync(async () =>
        {
            IsDeviceConnected = await  _adbService.CheckDeviceConnectionAsync();
            await _downloaderService.RefreshDownloadedGamesAsync();
        });
    }

    private void OnDownloadedGamesListChanged()
    {
        _downloadedGamesSourceCache.Edit(innerCache =>
        {
            innerCache.Clear();
            innerCache.AddOrUpdate(_downloaderService.DownloadedGames);
        });
    }
    
    private IObservable<Unit> InstallImpl()
    {
        return Observable.Start(() =>
        {
            var selectedGames = _downloadedGamesSourceCache.Items.Where(game => game.IsSelected).ToList();
            if (selectedGames.Count == 0)
            {
                Log.Information("No downloaded games selected for install");
                Globals.ShowNotification(Resources.InstallNoun, Resources.NoGamesSelected, NotificationType.Information,
                    TimeSpan.FromSeconds(2));
                return;
            }

            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                if (string.IsNullOrEmpty(game.ReleaseName))
                {
                    Log.Warning("Game {Game} has no release name, not installing", game);
                    continue;
                }
                game.Install();
            }
        });
    }
    
    private IObservable<Unit> DeleteImpl()
    {
        return Observable.Start(() =>
        {
            var selectedGames = _downloadedGamesSourceCache.Items.Where(game => game.IsSelected).ToList();
            if (selectedGames.Count == 0)
            {
                Log.Information("No games selected for deletion");
                Globals.ShowNotification(Resources.Delete, Resources.NoGamesSelected, NotificationType.Information,
                    TimeSpan.FromSeconds(2));
                return;
            }

            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                _downloaderService.DeleteDownloadedGameAsync(game);
            }
            Refresh.Execute().Subscribe();
        });
    }
    
    private void OnDeviceStateChanged(DeviceState state)
    {
        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
        IsDeviceConnected = state switch
        {
            DeviceState.Online => true,
            DeviceState.Offline => false,
            _ => IsDeviceConnected
        };
    }
    }