using System;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Windows.Input;
using AdvancedSharpAdbClient;
using Avalonia.Threading;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.Services;
using QSideloader.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly AdbService _adbService;
    public MainWindowViewModel()
    {
        _adbService = ServiceContainer.AdbService;
        ShowDialog = new Interaction<GameDetailsViewModel, GameViewModel?>();
        ShowGameDetailsCommand = ReactiveCommand.CreateFromTask<Game>(async game =>
        {
            if (Globals.AvailableGames is null) return;
            Log.Debug("Opening game details dialog for {GameName}", game.GameName);
            var gameDetails = new GameDetailsViewModel(game);
            await ShowDialog.Handle(gameDetails);
        });
        _adbService.WhenDeviceChanged.Subscribe(OnDeviceChanged);
        IsDeviceConnected = _adbService.CheckDeviceConnection();
    }

    public void EnqueueTask(Game game, TaskType taskType)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var taskView = new TaskView(game, taskType);
            taskView.Run();
            TaskList.Add(taskView);
        });
        Log.Information("Enqueued task {TaskType} {TaskName}", taskType, game.GameName);
    }
    
    private void OnDeviceChanged(AdbService.AdbDevice device)
    {
        switch (device.State)
        {
            case DeviceState.Online:
                IsDeviceConnected = true;
                break;
            case DeviceState.Offline:
                IsDeviceConnected = false;
                break;
        }
    }

    [Reactive] public bool IsDeviceConnected { get; set; }
    [Reactive] public ObservableCollection<TaskView> TaskList { get; set; } = new();

    public ICommand ShowGameDetailsCommand { get; }
    public Interaction<GameDetailsViewModel, GameViewModel?> ShowDialog { get; }
}