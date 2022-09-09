using System;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using Avalonia.Controls.Notifications;
using QSideloader.Models;
using QSideloader.Services;
using QSideloader.Utilities;
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
    private readonly Backup? _backup;
    private readonly SideloaderSettingsViewModel _sideloaderSettings;
    private readonly TaskType _taskType;
    private string? _path;
    private readonly BackupOptions? _backupOptions;

    // Dummy constructor for XAML, do not use
    public TaskViewModel()
    {
        _adbService = AdbService.Instance;
        _adbDevice = _adbService.Device;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        _game = new Game("GameName", "ReleaseName", 1337, "NoteText");
        TaskId = new TaskId();
        TaskName = "TaskName";
        GameName = "GameName";
        DownloadStats = "DownloadStats";
        RunTask = ReactiveCommand.Create(() => { Hint = "Click to cancel"; });
        Activator = new ViewModelActivator();
    }

    public TaskViewModel(TaskOptions taskOptions)
    {
        _adbService = AdbService.Instance;
        _adbDevice = _adbService.Device!;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        TaskId = new TaskId();
        _taskType = taskOptions.Type;
        Func<Task> action;
        Activator = new ViewModelActivator();
        switch (taskOptions.Type)
        {
            case TaskType.DownloadAndInstall:
                _game = taskOptions.Game ?? throw new ArgumentException("Game not specified for DownloadAndInstall task");
                TaskName = _game.GameName ?? "N/A";
                action = RunDownloadAndInstallAsync;
                break;
            case TaskType.DownloadOnly:
                _game = taskOptions.Game ?? throw new ArgumentException("Game not specified for DownloadOnly task");
                TaskName = _game.GameName ?? "N/A";
                action = RunDownloadOnlyAsync;
                break;
            case TaskType.InstallOnly:
                _game = taskOptions.Game ?? throw new ArgumentException("Game not specified for InstallOnly task");
                _path = taskOptions.Path ?? throw new ArgumentException("Game path not specified for InstallOnly task");
                TaskName = _game.GameName ?? "N/A";
                action = RunInstallOnlyAsync;
                break;
            case TaskType.Uninstall:
                if (taskOptions.Game is null && taskOptions.App is null)
                    throw new ArgumentException("Game or App not specified for Uninstall task");
                if (taskOptions.Game is not null && taskOptions.App is not null)
                    throw new ArgumentException("Game and App both specified for Uninstall task");
                _game = taskOptions.Game;
                _app = taskOptions.App;
                TaskName = _game?.GameName ?? _app?.Name ?? "N/A";
                action = RunUninstallAsync;
                break;
            case TaskType.BackupAndUninstall:
                _game = taskOptions.Game ?? throw new ArgumentException("Game not specified for BackupAndUninstall task");
                _backupOptions = taskOptions.BackupOptions ?? throw new ArgumentException("Backup options not specified for BackupAndUninstall task");
                TaskName = _game.GameName ?? "N/A";
                action = RunBackupAndUninstallAsync;
                break;
            case TaskType.Backup:
                _game = taskOptions.Game ?? throw new ArgumentException("Game not specified for Backup task");
                _backupOptions = taskOptions.BackupOptions ?? throw new ArgumentException("Backup options not specified for Backup task");
                TaskName = _game.GameName ?? "N/A";
                action = RunBackupAsync;
                break;
            case TaskType.Restore:
                _backup = taskOptions.Backup ?? throw new ArgumentException("Backup not specified for Restore task");
                TaskName = _backup.Name;
                action = RunRestoreAsync;
                break;
            case TaskType.PullAndUpload:
                _app = taskOptions.App ?? throw new ArgumentException("App not specified for PullAndUpload task");
                TaskName = _app.Name;
                action = RunPullAndUploadAsync;
                break;
            case TaskType.InstallTrailersAddon:
                _path = taskOptions.Path;
                action = RunInstallTrailersAddonAsync;
                TaskName = "Trailers addon";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(taskOptions), "Unknown task type");
        }
        RunTask = ReactiveCommand.CreateFromTask(async () =>
        {
            Hint = "Click to cancel";
            await action();
        });
        RunTask.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Task {TaskId} {TaskType} {TaskName} failed", TaskId, _taskType, TaskName);
            if (!IsFinished)
                OnFinished($"Task failed: {ex.Message}", false, ex);
        });
    }

    public ReactiveCommand<Unit, Unit> RunTask { get; }

    public TaskId TaskId { get; }
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
    
    private void RefreshDownloadStats((double bytesPerSecond, long downloadedBytes, long totalBytes) stats)
    {
        var speedMBytes = Math.Round(stats.bytesPerSecond / 1000000, 2);
        var progressPercent = Math.Floor((double)stats.downloadedBytes / stats.totalBytes * 100);

        DownloadStats = $"{progressPercent}%, {speedMBytes}MB/s";
    }

    private async Task RunDownloadAndInstallAsync()
    {
        EnsureDeviceConnected(true);
        await DoCancellableAsync(async () =>
        {
            _path = await DownloadAsync();
        }, "Download failed");

        // successStatus is not set deliberately
        await DoCancellableAsync(async () =>
        {
            var deleteAfterInstall =
                _sideloaderSettings.DownloadsPruningPolicy == DownloadsPruningPolicy.DeleteAfterInstall;
            await InstallAsync(_path ?? throw new InvalidOperationException("path is null"),
                deleteAfterInstall);
        }, "Install failed");
    }

    private async Task RunDownloadOnlyAsync()
    {
        await DoCancellableAsync(async () =>
        {
            _path = await DownloadAsync();
        }, "Download failed", "Downloaded");
    }

    private async Task RunInstallOnlyAsync()
    {
        EnsureDeviceConnected(true);
        // successStatus is not set deliberately
        await DoCancellableAsync(async () =>
        {
            _ = _path ?? throw new InvalidOperationException("path is null");
            var deleteAfterInstall = _path.StartsWith(_sideloaderSettings.DownloadsLocation) &&
                                     _sideloaderSettings.DownloadsPruningPolicy ==
                                     DownloadsPruningPolicy.DeleteAfterInstall;
            await InstallAsync(_path, deleteAfterInstall);
        }, "Install failed");
    }

    private async Task RunUninstallAsync()
    {
        EnsureDeviceConnected(true);
        await DoCancellableAsync(async () =>
        {
            await UninstallAsync();
        }, "Uninstall failed", "Uninstalled");
    }

    private async Task RunBackupAndUninstallAsync()
    {
        EnsureDeviceConnected(true);
        await DoCancellableAsync(async () =>
        {
            await BackupAsync();
            await UninstallAsync();
        }, "Uninstall failed", "Uninstalled");
    }

    private async Task RunBackupAsync()
    {
        EnsureDeviceConnected(true);
        await DoCancellableAsync(async () =>
        {
            await BackupAsync();
        }, "Backup failed", "Backup created");
    }

    private async Task RunRestoreAsync()
    {
        EnsureDeviceConnected(true);
        await DoCancellableAsync(async () =>
        {
            await RestoreAsync(_backup!);
        }, "Restore failed", "Backup restored");
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
                var exception = t.Exception!.InnerExceptions.FirstOrDefault() ?? t.Exception;
                Log.Error(exception, "Failed to pull and upload");
                OnFinished("Donation failed", false, exception);
                throw t.Exception!;
            }
            if (t.IsCanceled)
                OnFinished("Cancelled");
            else if (t.IsCompletedSuccessfully)
                OnFinished("Uploaded");
        });
    }

    private async Task RunInstallTrailersAddonAsync()
    {
        await DoCancellableAsync(async () =>
        {
            if (Directory.Exists(PathHelper.TrailersPath) && !File.Exists(_path))
            {
                OnFinished("Already installed");
                return;
            }

            await InstallTrailersAddonAsync();
        }, "Install failed", "Installed");
    }
    
    private async Task DoCancellableAsync(Func<Task> func, string failureStatus, string? successStatus = null)
    {
        try
        {
            await func();
            if (!string.IsNullOrEmpty(successStatus))
                OnFinished(successStatus);
        }
        catch (OperationCanceledException)
        {
            OnFinished("Cancelled");
        }
        catch (Exception e)
        {
            OnFinished(failureStatus, false, e);
        }
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
            if (_game?.ReleaseName is not null)
                _downloaderService.PruneDownloadedVersions(_game.ReleaseName);
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
        _adbDevice!.SideloadGame(_game!, gamePath, _cancellationTokenSource.Token)
            .SubscribeOn(RxApp.TaskpoolScheduler)
            .Subscribe(
                x => Status = x,
                e =>
                {
                    AdbService.ReleasePackageOperationLock();
                    if (e is OperationCanceledException)
                        OnFinished("Cancelled");
                    else
                        OnFinished("Install failed", false, e);
                },
                () =>
                {
                    AdbService.ReleasePackageOperationLock();
                    if (deleteAfterInstall && Directory.Exists(gamePath))
                    {
                        Log.Information("Deleting downloaded files from {Path}", gamePath);
                        Status = "Deleting downloaded files";
                        try
                        {
                            Directory.Delete(gamePath, true);
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "Failed to delete downloaded files");
                            // Treat as success because the installation is still successful
                            OnFinished("Failed to delete downloaded files");
                        }
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
            if (_game is not null)
                await Task.Run(() => _adbDevice!.UninstallPackage(_game.PackageName));
            else if (_app is not null)
                await Task.Run(() => _adbDevice!.UninstallPackage(_app.PackageName));
            else
                throw new InvalidOperationException("Both game and app are null");
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
        await Task.Run(() => _adbDevice!.CreateBackup(_game!.PackageName!, _backupOptions!, _cancellationTokenSource.Token));
    }
    
    private async Task RestoreAsync(Backup backup)
    {
        Status = "Restore queued";
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            EnsureDeviceConnected();
            Status = "Restoring backup";
            await Task.Run(() => _adbDevice!.RestoreBackup(backup));
        }
        finally
        {
            AdbService.ReleasePackageOperationLock();
        }
    }

    private async Task InstallTrailersAddonAsync()
    {
        Status = "Downloading";
        if (!File.Exists(_path))
        {
            var progress = new DelegateProgress<(double bytesPerSecond, long downloadedBytes, long totalBytes)>(RefreshDownloadStats);
            _path = await _downloaderService.DownloadTrailersAddon(progress, _cancellationTokenSource.Token);
        }
        Status = "Installing";
        DownloadStats = "";
        await GeneralUtils.InstallTrailersAddonAsync(_path, true);
        OnFinished("Installed");
    }

    private void OnFinished(string status, bool isSuccess = true, Exception? e = null)
    {
        if (IsFinished)
            return;
        Hint = "Click to dismiss";
        IsFinished = true;
        Status = status;
        DownloadStats = "";
        Log.Information("Task {TaskId} {TaskType} {TaskName} finished. Result: {Status}",
            TaskId, _taskType, TaskName, status);
        if (isSuccess) return;
        if (e is not null)
            Globals.ShowErrorNotification(e, $"Task \"{TaskName}\" failed");
        else
            Globals.ShowNotification("Error", $"Task \"{TaskName}\" failed", NotificationType.Error,
                TimeSpan.Zero);
    }

    public void Cancel()
    {
        if (_cancellationTokenSource.IsCancellationRequested || IsFinished) return;
        _cancellationTokenSource.Cancel();
        Log.Information("Requested cancellation of task {TaskType} {TaskName}", _taskType, TaskName);
    }

    /// <summary>
    /// Ensure that the device is connected and it's the correct device.
    /// </summary>
    /// <param name="simpleCheck">Use simple connection check.</param>
    /// <exception cref="InvalidOperationException">Thrown if device is not connected.</exception>
    /// <remarks>First call with <c>simpleCheck=true</c> ties the task to current device.</remarks>
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
        OnFinished("Failed: no device connection", false);
        throw new InvalidOperationException("No device connection");
    }
}