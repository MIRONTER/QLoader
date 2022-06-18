using System;
using System.IO;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class TaskViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly AdbService _adbService;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly DownloaderService _downloaderService;
    private readonly Game _game;
    private readonly SideloaderSettingsViewModel _sideloaderSettings;
    private readonly TaskType _taskType;
    private string? _gamePath;

    // Dummy constructor for XAML, do not use
    public TaskViewModel()
    {
        _adbService = AdbService.Instance;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        _game = new Game("GameName", "ReleaseName", 1337, "NoteText");
        GameName = "GameName";
        DownloadStats = "DownloadStats";
        RunTask = ReactiveCommand.CreateFromTask(RunTaskImpl);
        Activator = new ViewModelActivator();
    }

    public TaskViewModel(Game game, TaskType taskType)
    {
        if (taskType is TaskType.InstallOnly or TaskType.Restore)
        {
            // We need a game path to run installation/restore a backup
            Log.Error("Game path not specified for {TaskType} task", taskType);
            throw new ArgumentException($"Game path not specified for {taskType} task");
        }
        _adbService = AdbService.Instance;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        _game = game;
        GameName = game.GameName;
        _taskType = taskType;
        RunTask = ReactiveCommand.CreateFromTask(RunTaskImpl);
        RunTask.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error("Task {TaskType} {TaskName} failed with error: {Message}", _taskType, TaskName, ex.Message);
            Log.Verbose(ex, "Task {TaskType} failed with exception", _taskType);
            if (!IsFinished)
                OnFinished($"Task failed: {ex.Message}");
        });
        Activator = new ViewModelActivator();
    }

    public TaskViewModel(Game game, TaskType taskType, string gamePath)
    {
        if (taskType is not (TaskType.InstallOnly or TaskType.Restore))
            Log.Warning("Unneeded game path specified for {TaskType} task", taskType);
        else
            _gamePath = gamePath;
        _adbService = AdbService.Instance;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        _game = game;
        GameName = game.GameName;
        _taskType = taskType;
        RunTask = ReactiveCommand.CreateFromTask(RunTaskImpl);
        Activator = new ViewModelActivator();
    }

    public ReactiveCommand<Unit, Unit> RunTask { get; }

    public string TaskName => _game.GameName ?? "N/A";
    public bool IsFinished { get; private set; }
    public string? GameName { get; }
    [Reactive] public string Status { get; private set; } = "Status";
    [Reactive] public string DownloadStats { get; private set; } = "";
    [Reactive] public string Hint { get; private set; } = "";

    public ViewModelActivator Activator { get; }

    private void RefreshDownloadStats((float downloadSpeedBytes, double downloadedBytes)? stats)
    {
        if (stats is null)
        {
            DownloadStats = "";
            return;
        }
        
        var speedMBytes = Math.Round((double) stats.Value.downloadSpeedBytes / 1000000, 2);
        var downloadedMBytes = Math.Round( stats.Value.downloadedBytes / 1000000, 2);
        var progressPercent = Math.Min(Math.Floor(downloadedMBytes / _game.GameSize * 97), 100);

        DownloadStats = $"{progressPercent}%, {speedMBytes}MB/s";
    }

    private async Task RunTaskImpl()
    {
        Hint = "Click to cancel";
        switch (_taskType)
        {
            case TaskType.DownloadAndInstall:
                await RunDownloadAndInstallAsync();
                break;
            case TaskType.DownloadOnly:
                await RunDownloadOnlyAsync();
                break;
            case TaskType.InstallOnly:
                await RunInstallOnlyAsync();
                break;
            case TaskType.Uninstall:
                await RunUninstallAsync();
                break;
            case TaskType.BackupAndUninstall:
                await RunBackupAndUninstallAsync();
                break;
            case TaskType.Backup:
                await RunBackupAsync();
                break;
            case TaskType.Restore:
                await RunRestoreAsync();
                break;
            case TaskType.PullAndUpload:
                await RunPullAndUploadAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(_taskType));
        }
    }

    private async Task RunDownloadAndInstallAsync()
    {
        EnsureDeviceConnection(true);
        try
        {
            _gamePath = await DownloadAsync();
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException or TaskCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Download failed");
                throw;
            }

            return;
        }

        try
        {
            await Install(_gamePath ?? throw new InvalidOperationException("gamePath is null"),
                _sideloaderSettings.DeleteAfterInstall);
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException or TaskCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Install failed");
                throw;
            }
        }
    }

    private async Task RunDownloadOnlyAsync()
    {
        try
        {
            _gamePath = await DownloadAsync();
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException or TaskCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Download failed");
                throw;
            }

            return;
        }

        OnFinished("Downloaded");
    }

    private async Task RunInstallOnlyAsync()
    {
        EnsureDeviceConnection(true);
        try
        {
            await Install(_gamePath ?? throw new InvalidOperationException("gamePath is null"));
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException or TaskCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Install failed");
                throw;
            }
        }
    }

    private async Task RunUninstallAsync()
    {
        EnsureDeviceConnection(true);
        try
        {
            await Uninstall();
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException or TaskCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Uninstall failed");
                throw;
            }
        }
    }

    private async Task RunBackupAndUninstallAsync()
    {
        EnsureDeviceConnection(true);
        try
        {
            await Backup();
            await Uninstall();
            OnFinished("Uninstalled");
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException or TaskCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Uninstall failed");
                throw;
            }
        }
    }

    private async Task RunBackupAsync()
    {
        EnsureDeviceConnection(true);
        try
        {
            await Backup();
            OnFinished("Backup created");
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException or TaskCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Backup failed");
                throw;
            }
        }
    }

    private async Task RunRestoreAsync()
    {
        EnsureDeviceConnection(true);
        try
        {
            await Restore(_gamePath!);
            OnFinished("Backup restored");
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException or TaskCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Restore failed");
                throw;
            }
        }
    }

    private async Task RunPullAndUploadAsync()
    {
        EnsureDeviceConnection();
        /*try
        {
            Status = "Pulling";
            var localPath = await Pull();
            Status = "Uploading";
            await Task.Run(() => _downloaderService.UploadDonation(_game, localPath));
            OnFinished("Uploaded");
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException or TaskCanceledException)
                OnFinished("Cancelled");
            else
            {
                OnFinished("Upload failed");
                throw;
            }
        }*/
    }

    private async Task<string> DownloadAsync()
    {
        var downloadStatsSubscription = Disposable.Empty;
        Status = "Download queued";
        await DownloaderService.TakeDownloadLockAsync(_cancellationTokenSource.Token);
        try
        {
            Status = "Downloading";
            downloadStatsSubscription = _downloaderService
                .PollStats(TimeSpan.FromMilliseconds(100), ThreadPoolScheduler.Instance)
                .SubscribeOn(RxApp.TaskpoolScheduler)
                .Subscribe(RefreshDownloadStats);
            var gamePath = await _downloaderService.DownloadGameAsync(_game, _cancellationTokenSource.Token);
            downloadStatsSubscription.Dispose();
            return gamePath;
        }
        finally
        {
            DownloadStats = "";
            downloadStatsSubscription.Dispose();
            DownloaderService.ReleaseDownloadLock();
        }
    }

    private async Task Install(string gamePath, bool deleteAfterInstall = false)
    {
        Status = "Install queued";
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            EnsureDeviceConnection();
        }
        catch (InvalidOperationException)
        {
            AdbService.ReleasePackageOperationLock();
            throw;
        }
        Status = "Installing";

        // Here I assume that Install is the last step in the process, this might change in the future
        _adbService.Device!.SideloadGame(_game, gamePath)
            .SubscribeOn(RxApp.TaskpoolScheduler)
            .Subscribe(
                x => Status = x,
                _ =>
                {
                    AdbService.ReleasePackageOperationLock();
                    OnFinished("Install failed");
                },
                () =>
                {
                    AdbService.ReleasePackageOperationLock();
                    if (deleteAfterInstall)
                    {
                        Log.Information("Deleting downloaded files from {Path}", gamePath);
                        Status = "Deleting downloaded files";
                        Directory.Delete(gamePath, true);
                    }

                    OnFinished("Installed");
                });
    }

    private async Task Uninstall()
    {
        Status = "Uninstall queued";
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            EnsureDeviceConnection();
            Status = "Uninstalling";
            await Task.Run(() => _adbService.Device!.UninstallGame(_game));
        }
        finally
        {
            AdbService.ReleasePackageOperationLock();
        }
    }

    private async Task Backup()
    {
        EnsureDeviceConnection();
        Status = "Creating backup";
        await Task.Run(() => _adbService.Device!.CreateBackup(_game));
    }
    
    private async Task Restore(string backupPath)
    {
        Status = "Restore queued";
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            EnsureDeviceConnection();
            Status = "Restoring backup";
            await Task.Run(() => _adbService.Device!.RestoreBackup(backupPath));
        }
        finally
        {
            AdbService.ReleasePackageOperationLock();
        }
    }

    private void OnFinished(string status)
    {
        if (IsFinished)
        {
            Log.Warning("Attempted to finish task {TaskType} {TaskName} which is already finished", _taskType,
                TaskName);
            return;
        }
        Hint = "Click to dismiss";
        IsFinished = true;
        Status = status;
        Log.Information("Task {TaskType} {TaskName} finished. Result: {Status}",
            _taskType, _game.GameName, status);
    }

    public void Cancel()
    {
        if (_cancellationTokenSource.IsCancellationRequested || IsFinished) return;
        _cancellationTokenSource.Cancel();
        Log.Information("Requested cancellation of task {TaskType} {TaskName}", _taskType, _game.GameName);
    }

    private void EnsureDeviceConnection(bool simpleCheck = false)
    {
        if ((simpleCheck && _adbService.CheckDeviceConnectionSimple()) ||
            (!simpleCheck && _adbService.CheckDeviceConnection())) return;
        OnFinished("Failed: no device connection");
        throw new InvalidOperationException("No device connection");
    }
}

public enum TaskType
{
    DownloadAndInstall,
    DownloadOnly,
    InstallOnly,
    Uninstall,
    BackupAndUninstall,
    Backup,
    Restore,
    PullAndUpload
}