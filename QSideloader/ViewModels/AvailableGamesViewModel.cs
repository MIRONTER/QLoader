using System;
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

public class AvailableGamesViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly ObservableAsPropertyHelper<bool> _isBusy;

    public AvailableGamesViewModel()
    {
        Activator = new ViewModelActivator();
        Refresh = ReactiveCommand.CreateFromObservable(RefreshImpl);
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Install = ReactiveCommand.CreateFromObservable(InstallImpl);
        Refresh.Execute().Subscribe();
    }

    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> Install { get; }
    [Reactive] public ObservableCollection<Game> AvailableGames { get; set; } = new();
    public bool IsBusy => _isBusy.Value;
    [Reactive] public bool MultiSelectEnabled { get; set; } = true;
    private bool FirstRefresh { get; set; } = true;
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
            if (!ServiceContainer.ADBService.ValidateDeviceConnection())
            {
                Log.Warning("InstallImpl: no device connection!");
                return;
            }

            var selectedGames = AvailableGames.Where(game => game.IsSelected);
            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                QueueForInstall(game);
            }
        });
    }

    public void QueueForInstall(Game game)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var taskView = new TaskView();
            taskView.ViewModel!.Game = game;
            taskView.ViewModel!.PerformTask.Execute().Subscribe();
            Globals.MainWindowViewModel!.TaskList.Add(taskView);
        });
        Log.Information("Queued for install: {ReleaseName}", game.ReleaseName);
    }

    private void RefreshAvailableGames(bool redownload = false)
    {
        ServiceContainer.DownloaderService.EnsureGameListAvailableAsync(redownload).GetAwaiter().GetResult();
        RefreshProps();
    }

    private void RefreshProps()
    {
        Dispatcher.UIThread.InvokeAsync(PopulateAvailableGames);
    }

    private void PopulateAvailableGames()
    {
        if (Globals.AvailableGames is null)
        {
            Log.Warning("PopulateAvailableGames: Globals.AvailableGames is not initialized!");
            return;
        }

        var toAdd = Globals.AvailableGames.Except(AvailableGames).ToList();
        var toRemove = AvailableGames.Except(Globals.AvailableGames).ToList();
        foreach (var addedGame in toAdd)
            AvailableGames.Add(addedGame);
        foreach (var removedGame in toRemove)
            AvailableGames.Remove(removedGame);
    }

    public void ResetSelections()
    {
        // ReSharper disable once ForCanBeConvertedToForeach
        for (var i = 0; i < AvailableGames.Count; i++) AvailableGames[i].IsSelected = false;
    }
}