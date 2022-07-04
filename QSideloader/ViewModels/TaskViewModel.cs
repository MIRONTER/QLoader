using System;
using System.IO;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
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
    private AdbService.AdbDevice? _adbDevice;
    private bool _ensuredDeviceConnected;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly DownloaderService _downloaderService;
    private readonly Game? _game;
    private readonly InstalledApp? _app;
    private readonly SideloaderSettingsViewModel _sideloaderSettings;
    private readonly TaskType _taskType;
    private string? _gamePath;

    // Dummy constructor for XAML, do not use
    public TaskViewModel()
    {
        _adbService = AdbService.Instance;
        _adbDevice = _adbService.Device;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        _game = new Game("GameName", "ReleaseName", 1337, "NoteText");
        TaskName = "TaskName";
        GameName = "GameName";
        DownloadStats = "DownloadStats";
        RunTask = ReactiveCommand.CreateFromTask(RunTaskImpl);
        Activator = new ViewModelActivator();
    }

    public TaskViewModel(Game game, TaskType taskType)
    {
        switch (taskType)
        {
            case TaskType.InstallOnly or TaskType.Restore:
                // We need a game path to run installation/restore a backup
                Log.Error("Game path not specified for {TaskType} task", taskType);
                throw new ArgumentException($"Game path not specified for {taskType} task");
            case TaskType.PullAndUpload:
                throw new InvalidOperationException("Wrong constructor for PullAndUpload task");
        }

        _adbService = AdbService.Instance;
        _adbDevice = _adbService.Device;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        _game = game;
        TaskName = _game?.GameName ?? "N/A";
        GameName = game.GameName;
        _taskType = taskType;
        RunTask = ReactiveCommand.CreateFromTask(RunTaskImpl);
        RunTask.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Task {TaskType} {TaskName} failed", _taskType, TaskName);
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
        if (taskType is TaskType.PullAndUpload)
            throw new InvalidOperationException("Wrong constructor for PullAndUpload task");
        _adbService = AdbService.Instance;
        _adbDevice = _adbService.Device;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        _game = game;
        TaskName = _game?.GameName ?? "N/A";
        GameName = game.GameName;
        _taskType = taskType;
        RunTask = ReactiveCommand.CreateFromTask(RunTaskImpl);
        Activator = new ViewModelActivator();
    }

    public TaskViewModel(InstalledApp app, TaskType taskType)
    {
        if (taskType is not TaskType.PullAndUpload)
            throw new InvalidOperationException("This constructor is only for PullAndUpload task");
        _adbService = AdbService.Instance;
        _adbDevice = _adbService.Device!;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        _app = app;
        TaskName = _app?.Name ?? "N/A";
        _taskType = taskType;
        RunTask = ReactiveCommand.CreateFromTask(RunTaskImpl);
        Activator = new ViewModelActivator();
    }

    public ReactiveCommand<Unit, Unit> RunTask { get; }

    public string TaskName { get; }
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
        var progressPercent = Math.Min(Math.Floor(downloadedMBytes / _game!.GameSize * 97), 100);

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
        EnsureDeviceConnected(true);
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
            await InstallAsync(_gamePath ?? throw new InvalidOperationException("gamePath is null"),
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
        EnsureDeviceConnected(true);
        try
        {
            await InstallAsync(_gamePath ?? throw new InvalidOperationException("gamePath is null"));
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
        EnsureDeviceConnected(true);
        try
        {
            await UninstallAsync();
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
        EnsureDeviceConnected(true);
        try
        {
            await BackupAsync();
            await UninstallAsync();
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
        EnsureDeviceConnected(true);
        try
        {
            await BackupAsync();
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
        EnsureDeviceConnected(true);
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
        EnsureDeviceConnected();
        await Task.Run(async () =>
        {
            Status = "Pulling from device";
            var path = _adbDevice!.PullApp(_app!.PackageName, "donations");
            Status = "Preparing to upload";
            var apkInfo = GeneralUtils.GetApkInfo(Path.Combine(path, _app.PackageName + ".apk"));
            var archiveName = $"{apkInfo.ApplicationLabel} v{apkInfo.VersionCode} {apkInfo.PackageName}.zip";
            await File.WriteAllTextAsync(Path.Combine(path, "HWID.txt"),
                GeneralUtils.GetHwid());
            await ZipUtil.CreateArchiveAsync(path, "donations",
                archiveName, _cancellationTokenSource.Token);
            Directory.Delete(path, true);
            Status = "Uploading";
            await _downloaderService.UploadDonationAsync(Path.Combine("donations", archiveName), _cancellationTokenSource.Token);
            Globals.MainWindowViewModel!.OnGameDonated(apkInfo.PackageName, apkInfo.VersionCode);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Log.Error(t.Exception!, "Failed to pull and upload");
                OnFinished("Donation failed");
                throw t.Exception!;
            }
            if (t.IsCanceled)
                OnFinished("Cancelled");
            else if (t.IsCompletedSuccessfully)
                OnFinished("Uploaded");
        });
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
            var gamePath = await _downloaderService.DownloadGameAsync(_game!, _cancellationTokenSource.Token);
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

    private async Task InstallAsync(string gamePath, bool deleteAfterInstall = false)
    {
        Status = "Install queued";
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            EnsureDeviceConnected();
        }
        catch (InvalidOperationException)
        {
            AdbService.ReleasePackageOperationLock();
            throw;
        }
        Status = "Installing";

        // Here I assume that Install is the last step in the process, this might change in the future
        _adbDevice!.SideloadGame(_game!, gamePath)
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

    private async Task UninstallAsync()
    {
        Status = "Uninstall queued";
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            EnsureDeviceConnected();
            Status = "Uninstalling";
            await Task.Run(() => _adbDevice!.UninstallGame(_game!));
        }
        finally
        {
            AdbService.ReleasePackageOperationLock();
        }
    }

    private async Task BackupAsync()
    {
        EnsureDeviceConnected();
        Status = "Creating backup";
        await Task.Run(() => _adbDevice!.CreateBackup(_game!));
    }
    
    private async Task Restore(string backupPath)
    {
        Status = "Restore queued";
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            EnsureDeviceConnected();
            Status = "Restoring backup";
            await Task.Run(() => _adbDevice!.RestoreBackup(backupPath));
        }
        finally
        {
            AdbService.ReleasePackageOperationLock();
        }
    }

    private void OnFinished(string status)
    {
        if (IsFinished)
            return;
        Hint = "Click to dismiss";
        IsFinished = true;
        Status = status;
        Log.Information("Task {TaskType} {TaskName} finished. Result: {Status}",
            _taskType, TaskName, status);
    }

    public void Cancel()
    {
        if (_cancellationTokenSource.IsCancellationRequested || IsFinished) return;
        _cancellationTokenSource.Cancel();
        Log.Information("Requested cancellation of task {TaskType} {TaskName}", _taskType, TaskName);
    }

    private void EnsureDeviceConnected(bool simpleCheck = false)
    {
        if (!_ensuredDeviceConnected)
        {
            if ((simpleCheck && _adbService.CheckDeviceConnectionSimple()) ||
                (!simpleCheck && _adbService.CheckDeviceConnection()))
            {
                // If user switched to another device during download, here we can safely assign the new device
                _adbDevice = _adbService.Device!;
                if (!simpleCheck)
                    _ensuredDeviceConnected = true;
                return;
            }
        }
        // If we have already ensured that a device is connected, we stick to that device
        else
        {
            if (_adbDevice is not null && 
                ((simpleCheck && _adbDevice.State == DeviceState.Online) ||
                (!simpleCheck && _adbService.PingDevice(_adbDevice)))
                ) 
                return;
        }
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