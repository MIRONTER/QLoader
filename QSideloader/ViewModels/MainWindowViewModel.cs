using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using AdvancedSharpAdbClient;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
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
    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly AdbService _adbService;
    public MainWindowViewModel()
    {
        _adbService = ServiceContainer.AdbService;
        ShowGameDetailsCommand = ReactiveCommand.CreateFromTask<Game>(async game =>
        {
            if (Globals.AvailableGames is null) return;
            Log.Debug("Opening game details dialog for {GameName}", game.GameName);
            var gameDetails = new GameDetailsViewModel(game);
            var dialog = new GameDetailsWindow(gameDetails);
            if (Application.Current is not null)
            {
                var mainWindow = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                    ?.MainWindow;
                await dialog.ShowDialog(mainWindow);
            }
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
    
    public IEnumerable<TaskView> GetTaskList()
    {
        return TaskList.ToList();
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
}