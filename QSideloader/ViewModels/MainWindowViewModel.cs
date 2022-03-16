using System;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel()
    {
        ShowDialog = new Interaction<GameDetailsViewModel, GameViewModel?>();
        ShowGameDetailsCommand = ReactiveCommand.CreateFromTask<Game>(async game =>
        {
            if (Globals.AvailableGames is null) return;
            var gameDetails = new GameDetailsViewModel(game);
            await ShowDialog.Handle(gameDetails);
        });
    }

    public void QueueForInstall(Game game)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var taskView = new TaskView();
            taskView.ViewModel!.Game = game;
            taskView.ViewModel!.PerformTask.Execute().Subscribe();
            TaskList.Add(taskView);
        });
        Log.Information("Queued for install: {ReleaseName}", game.ReleaseName);
    }

    [Reactive] public ObservableCollection<TaskView> TaskList { get; set; } = new();

    public ICommand ShowGameDetailsCommand { get; }
    public Interaction<GameDetailsViewModel, GameViewModel?> ShowDialog { get; }
}