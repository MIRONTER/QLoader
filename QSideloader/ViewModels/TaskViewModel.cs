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

// TODO: This whole class is a mess, need to refactor it.
// Upd: Maybe a bit better now?

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
            Log.Error(ex, "Task {TaskType} {TaskName} failed", _taskType, TaskName);
            if (!IsFinished)
                OnFinished($"Task failed: {ex.Message}", false, ex);
        });
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

    private async Task RunDownloadAndInstallAsync()
    {
        EnsureDeviceConnected(true);
        try
        {
            _path = await DownloadAsync();
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Download failed", false, e);
                throw;
            }

            return;
        }

        try
        {
            var deleteAfterInstall =
                _sideloaderSettings.DownloadsPruningPolicy == DownloadsPruningPolicy.DeleteAfterInstall;
            await InstallAsync(_path ?? throw new InvalidOperationException("path is null"),
                deleteAfterInstall);
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Install failed", false, e);
                throw;
            }
        }
    }

    private async Task RunDownloadOnlyAsync()
    {
        try
        {
            _path = await DownloadAsync();
            OnFinished("Downloaded");
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Download failed", false, e);
                throw;
            }
        }
    }

    private async Task RunInstallOnlyAsync()
    {
        EnsureDeviceConnected(true);
        try
        {
            _ = _path ?? throw new InvalidOperationException("path is null");
            var deleteAfterInstall = _path.StartsWith(_sideloaderSettings.DownloadsLocation) &&
                _sideloaderSettings.DownloadsPruningPolicy == DownloadsPruningPolicy.DeleteAfterInstall;
            await InstallAsync(_path, deleteAfterInstall);
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Install failed", false, e);
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
            OnFinished("Uninstalled");
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Uninstall failed", false, e);
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
            if (e is OperationCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Uninstall failed", false, e);
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
            if (e is OperationCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Backup failed", false, e);
                throw;
            }
        }
    }

    private async Task RunRestoreAsync()
    {
        EnsureDeviceConnected(true);
        try
        {
            await RestoreAsync(_backup!);
            OnFinished("Backup restored");
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Restore failed", false, e);
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
        try
        {
            if (Directory.Exists(PathHelper.TrailersPath) && !File.Exists(_path))
            {
                OnFinished("Already installed");
            }
            await InstallTrailersAddonAsync();
            OnFinished("Installed");
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException)
            {
                OnFinished("Cancelled");
            }
            else
            {
                OnFinished("Install failed", false, e);
                throw;
            }
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
        _adbDevice!.SideloadGame(_game!, gamePath)
            .SubscribeOn(RxApp.TaskpoolScheduler)
            .Subscribe(
                x => Status = x,
                e =>
                {
                    AdbService.ReleasePackageOperationLock();
                    OnFinished("Install failed", false, e);
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
        await Task.Run(() => _adbDevice!.CreateBackup(_game!, _backupOptions!));
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
            _path = await _downloaderService.DownloadTrailersAddon(_cancellationTokenSource.Token);
        Status = "Installing";
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
        Log.Information("Task {TaskType} {TaskName} finished. Result: {Status}",
            _taskType, TaskName, status);
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