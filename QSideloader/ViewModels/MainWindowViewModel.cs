using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    // ReSharper disable PrivateFieldCanBeConvertedToLocalVariable
    private readonly AdbService _adbService;
    private readonly DownloaderService _downloaderService;
    // ReSharper restore PrivateFieldCanBeConvertedToLocalVariable

    public MainWindowViewModel()
    {
        _adbService = AdbService.Instance;
        _downloaderService = DownloaderService.Instance;
        ShowGameDetailsCommand = ReactiveCommand.CreateFromTask<Game>(async game =>
        {
            if (_downloaderService.AvailableGames is null) return;
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

    [Reactive] public bool IsDeviceConnected { get; set; }
    [Reactive] public ObservableCollection<TaskView> TaskList { get; set; } = new();

    public ICommand ShowGameDetailsCommand { get; }

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
    
    public void EnqueueTask(Game game, TaskType taskType, string gamePath)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var taskView = new TaskView(game, taskType, gamePath);
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

    public void HandleDroppedFiles(IEnumerable<string> fileNames)
    {
        foreach (var fileName in fileNames)
        {
            if (Directory.Exists(fileName))
            {
                if (Directory.EnumerateFiles(fileName, ".backup", SearchOption.TopDirectoryOnly).Any())
                {
                    Log.Debug("Dropped folder {FileName} contains backup", fileName);
                    var dirName = Path.GetFileName(fileName);
                    var game = new Game(dirName, dirName);
                    EnqueueTask(game, TaskType.Restore, fileName);
                }
                if (Directory.EnumerateFiles(fileName, "*.apk", SearchOption.TopDirectoryOnly).Any())
                {
                    Log.Debug("Dropped folder {FileName} contains APK", fileName);
                    var dirName = Path.GetFileName(fileName);
                    Game game;
                    // Try to find OBB directory and set package name
                    var dirNames = Directory.EnumerateDirectories(fileName, "*.*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName);
                    var obbDirName = dirNames.Where(d => d is not null).FirstOrDefault(d => Regex.IsMatch(d!, @"^([A-Za-z]{1}[A-Za-z\d_]*\.)+[A-Za-z][A-Za-z\d_]*$"));
                    if (obbDirName is not null)
                    {
                        Log.Debug("Found OBB directory {ObbDirName}", obbDirName);
                        game = new Game(dirName, dirName, obbDirName);
                    }
                    else
                    {
                        game = new Game(dirName, dirName);
                    }
                    EnqueueTask(game, TaskType.InstallOnly, fileName);
                }
                else
                {
                    Log.Warning("Dropped directory {FileName} does not contain APK files and is not a backup",
                        fileName);
                }
            }
            else if (File.Exists(fileName) && fileName.EndsWith(".apk"))
            {
                Log.Debug("Dropped file {FileName} is an APK", fileName);
                var name = Path.GetFileName(fileName);
                var game = new Game(name, name);
                EnqueueTask(game, TaskType.InstallOnly, fileName);
            }
            else
            {
                Log.Warning("Unsupported dropped file {FileName}", fileName);
            }
        }
    }
}