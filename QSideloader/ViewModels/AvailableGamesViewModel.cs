using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using DynamicData;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class AvailableGamesViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly ObservableAsPropertyHelper<bool> _isBusy;
    private readonly AdbService _adbService;
    private readonly DownloaderService _downloaderService;
    private readonly SourceCache<Game, string> _availableGamesSourceCache = new(x => x.ReleaseName!);
    private readonly ReadOnlyObservableCollection<Game> _availableGames;

    public AvailableGamesViewModel()
    {
        _adbService = ServiceContainer.AdbService;
        _downloaderService = ServiceContainer.DownloaderService;
        Activator = new ViewModelActivator();

        Func<Game, bool> GameFilter(string text) => game => string.IsNullOrEmpty(text)
                                                            || text.Split()
                                                                .All(x => game.ReleaseName!.ToLower()
                                                                    .Contains(x.ToLower()));

        var filterPredicate = this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .DistinctUntilChanged()
            .Select(GameFilter);
        var cacheListBind = _availableGamesSourceCache.Connect()
            .RefCount()
            .ObserveOn(RxApp.TaskpoolScheduler)
            .Filter(filterPredicate)
            .SortBy(x => x.ReleaseName!)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _availableGames)
            .DisposeMany();
        this.WhenActivated(disposables =>
        {
            cacheListBind.Subscribe().DisposeWith(disposables);
        });
        Refresh = ReactiveCommand.CreateFromObservable(RefreshImpl);
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Install = ReactiveCommand.CreateFromObservable(InstallImpl);
        Refresh.Execute().Subscribe();
    }

    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> Install { get; }
    public ReadOnlyObservableCollection<Game> AvailableGames => _availableGames;
    public bool IsBusy => _isBusy.Value;
    [Reactive] public bool MultiSelectEnabled { get; set; } = true;
    private bool FirstRefresh { get; set; } = true;
    [Reactive] public string SearchText { get; set; } = "";
    public ViewModelActivator Activator { get; }

    private IObservable<Unit> RefreshImpl()
    {
        return Observable.Start(() =>
        {
            RefreshAvailableGames(!FirstRefresh);
            RefreshProps();
            FirstRefresh = false;
        });
    }

    private IObservable<Unit> InstallImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnection())
            {
                Log.Warning("InstallImpl: no device connection!");
                return;
            }

            var selectedGames = AvailableGames.Where(game => game.IsSelected);
            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                Globals.MainWindowViewModel!.QueueForInstall(game);
            }
        });
    }

    private void RefreshAvailableGames(bool redownload = false)
    {
        _downloaderService.EnsureGameListAvailableAsync(redownload).GetAwaiter().GetResult();
        RefreshProps();
    }

    private void RefreshProps()
    {
        PopulateAvailableGames();
    }

    private void PopulateAvailableGames()
    {
        if (Globals.AvailableGames is null)
        {
            Log.Warning("PopulateAvailableGames: Globals.AvailableGames is not initialized!");
            return;
        }

        //var toAdd = Globals.AvailableGames.Except(AvailableGames).ToList();
        var toRemove = AvailableGames.Except(Globals.AvailableGames).ToList();
        _availableGamesSourceCache.Edit(innerCache =>
        {
            innerCache.AddOrUpdate(Globals.AvailableGames);
            innerCache.Remove(toRemove);
        });
        // foreach (var addedGame in toAdd)
        //     AvailableGames.Add(addedGame);
        // foreach (var removedGame in toRemove)
        //     AvailableGames.Remove(removedGame);
    }

    public void ResetSelections()
    {
        // ReSharper disable once ForCanBeConvertedToForeach
        for (var i = 0; i < AvailableGames.Count; i++) AvailableGames[i].IsSelected = false;
    }
}