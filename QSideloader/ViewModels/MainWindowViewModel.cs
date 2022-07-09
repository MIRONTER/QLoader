using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
    private readonly SideloaderSettingsViewModel _sideloaderSettings;
    // ReSharper restore PrivateFieldCanBeConvertedToLocalVariable
    private readonly Subject<Unit> _gameDonateSubject = new ();

    public MainWindowViewModel()
    {
        _adbService = AdbService.Instance;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
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
        _adbService.WhenPackageListChanged.Subscribe(_ => RefreshGameDonationBadge());
        Task.Run(() => IsDeviceConnected = _adbService.CheckDeviceConnection());
    }

    [Reactive] public bool IsDeviceConnected { get; private set; }
    public ObservableCollection<TaskView> TaskList { get; } = new();
    [Reactive] public int DonatableAppsCount { get; private set; }
    public IObservable<Unit> WhenGameDonated => _gameDonateSubject.AsObservable();

    public ICommand ShowGameDetailsCommand { get; }

    public void EnqueueTask(Game game, TaskType taskType)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var taskView = new TaskView(game, taskType);
            TaskList.Add(taskView);
            taskView.Run();
            Log.Information("Enqueued task {TaskType} {TaskName}", taskType, taskView.TaskName);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Log.Error(t.Exception!, "Error while enqueuing task");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
    
    public void EnqueueTask(InstalledApp app, TaskType taskType)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var taskView = new TaskView(app, taskType);
            TaskList.Add(taskView);
            taskView.Run();
            Log.Information("Enqueued task {TaskType} {TaskName}", taskType, taskView.TaskName);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Log.Error(t.Exception!, "Error while enqueuing task");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
    
    public void EnqueueTask(Game game, TaskType taskType, string gamePath)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var taskView = new TaskView(game, taskType, gamePath);
            TaskList.Add(taskView);
            taskView.Run();
            Log.Information("Enqueued task {TaskType} {TaskName}", taskType, taskView.TaskName);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Log.Error(t.Exception!, "Error while enqueuing task");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
    
    public void EnqueueTask(TaskType taskType)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var taskView = new TaskView(taskType);
            TaskList.Add(taskView);
            taskView.Run();
            Log.Information("Enqueued task {TaskType} {TaskName}", taskType, taskView.TaskName);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Log.Error(t.Exception!, "Error while enqueuing task");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
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
        RefreshGameDonationBadge();
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
    
    public void RefreshGameDonationBadge()
    {
        DonatableAppsCount = _adbService.CheckDeviceConnectionSimple()
            ? _adbService.Device!.InstalledApps.Count(app => !app.IsHiddenFromDonation)
            : 0;
    }
    
    public void OnGameDonated(string packageName, int versionCode)
    {
        var existingDonatedPackage =
            _sideloaderSettings.DonatedPackages.FirstOrDefault(p => p.packageName == packageName);
        if (existingDonatedPackage != default)
        {
            _sideloaderSettings.DonatedPackages.Remove(existingDonatedPackage);
            existingDonatedPackage.Item2 = versionCode;
            _sideloaderSettings.DonatedPackages.Add(existingDonatedPackage);
        }
        else
            _sideloaderSettings.DonatedPackages.Add((packageName, versionCode));
        _adbService.Device?.RefreshInstalledApps();
        RefreshGameDonationBadge();
        _gameDonateSubject.OnNext(Unit.Default);
    }
}