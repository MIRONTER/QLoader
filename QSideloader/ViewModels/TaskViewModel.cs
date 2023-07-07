using System;
using System.Globalization;
using System.IO;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using Avalonia.Controls.Notifications;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Services;
using QSideloader.Utilities;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;
using Serilog.Context;

namespace QSideloader.ViewModels;

public class TaskViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly AdbService _adbService;
    private AdbService.AdbDevice? _adbDevice;
    private bool _ensuredDeviceConnected;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly DownloaderService _downloaderService;
    private readonly Game? _game;
    private long? _gameSizeBytes;
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
        ProgressStatus = "ProgressStatus";
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
                _game = taskOptions.Game ??
                        throw new ArgumentException(
                            $"Game not specified for {nameof(TaskType.DownloadAndInstall)} task");
                TaskName = _game.GameName ?? "N/A";
                action = RunDownloadAndInstallAsync;
                break;
            case TaskType.DownloadOnly:
                _game = taskOptions.Game ??
                        throw new ArgumentException($"Game not specified for {nameof(TaskType.DownloadOnly)} task");
                TaskName = _game.GameName ?? "N/A";
                action = RunDownloadOnlyAsync;
                break;
            case TaskType.InstallOnly:
                _game = taskOptions.Game ??
                        throw new ArgumentException($"Game not specified for {nameof(TaskType.InstallOnly)} task");
                _path = taskOptions.Path ??
                        throw new ArgumentException($"Game path not specified for {nameof(TaskType.InstallOnly)} task");
                TaskName = _game.GameName ?? "N/A";
                action = RunInstallOnlyAsync;
                break;
            case TaskType.Uninstall:
                if (taskOptions.Game is null && taskOptions.App is null)
                    throw new ArgumentException($"Game or App not specified for {nameof(TaskType.Uninstall)} task");
                if (taskOptions.Game is not null && taskOptions.App is not null)
                    throw new ArgumentException($"Game and App both specified for {nameof(TaskType.Uninstall)} task");
                _game = taskOptions.Game;
                _app = taskOptions.App;
                TaskName = _game?.GameName ?? _app?.Name ?? "N/A";
                action = RunUninstallAsync;
                break;
            case TaskType.BackupAndUninstall:
                _game = taskOptions.Game ??
                        throw new ArgumentException(
                            $"Game not specified for {nameof(TaskType.BackupAndUninstall)} task");
                _backupOptions = taskOptions.BackupOptions ??
                                 throw new ArgumentException(
                                     $"Backup options not specified for {nameof(TaskType.BackupAndUninstall)} task");
                TaskName = _game.GameName ?? "N/A";
                action = RunBackupAndUninstallAsync;
                break;
            case TaskType.Backup:
                _game = taskOptions.Game ??
                        throw new ArgumentException($"Game not specified for {nameof(TaskType.Backup)} task");
                _backupOptions = taskOptions.BackupOptions ??
                                 throw new ArgumentException(
                                     $"Backup options not specified for {nameof(TaskType.Backup)} task");
                TaskName = _game.GameName ?? "N/A";
                action = RunBackupAsync;
                break;
            case TaskType.Restore:
                _backup = taskOptions.Backup ??
                          throw new ArgumentException($"Backup not specified for {nameof(TaskType.Restore)} task");
                TaskName = _backup.Name;
                action = RunRestoreAsync;
                break;
            case TaskType.PullAndUpload:
                _app = taskOptions.App ??
                       throw new ArgumentException($"App not specified for {nameof(TaskType.PullAndUpload)} task");
                TaskName = _app.Name;
                action = RunPullAndUploadAsync;
                break;
            case TaskType.InstallTrailersAddon:
                _path = taskOptions.Path;
                action = RunInstallTrailersAddonAsync;
                TaskName = "Trailers addon";
                break;
            case TaskType.PullMedia:
                _path = taskOptions.Path ??
                        throw new ArgumentException($"Path not specified for {nameof(TaskType.PullMedia)}");
                action = RunPullMediaAsync;
                TaskName = "Pull pictures and videos";
                break;
            case TaskType.Extract:
                _app = taskOptions.App ??
                       throw new ArgumentException($"App not specified for {nameof(TaskType.Extract)} task");
                _path = taskOptions.Path ??
                        throw new ArgumentException($"Path not specified for {nameof(TaskType.Extract)}");
                TaskName = _app.Name;
                action = RunExtractAsync;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(taskOptions), "Unknown task type");
        }

        RunTask = ReactiveCommand.CreateFromTask(async () =>
        {
            Hint = Resources.ClickToCancel;
            await action();
        });
        RunTask.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Task {TaskId} {TaskType} {TaskName} failed", TaskId, _taskType, TaskName);
            if (!IsFinished)
                OnFinished($"{Resources.TaskFailed}: {ex.Message}", false, ex);
        });
    }

    public ReactiveCommand<Unit, Unit> RunTask { get; }

    public TaskId TaskId { get; }
    public string TaskName { get; }
    public bool IsFinished { get; private set; }
    public string? GameName { get; }
    [Reactive] public string Status { get; private set; } = "Status";
    [Reactive] public string ProgressStatus { get; private set; } = "";
    [Reactive] public string Hint { get; private set; } = "";

    public ViewModelActivator Activator { get; }

    private void RefreshDownloadStats((float downloadSpeedBytes, double downloadedBytes)? stats)
    {
        if (stats is null)
        {
            ProgressStatus = "";
            return;
        }

        double speedMBytes;
        double progressPercent;

        if (_gameSizeBytes is not null)
        {
            speedMBytes = Math.Round((double) stats.Value.downloadSpeedBytes / 1000000, 2);
            progressPercent = Math.Floor(stats.Value.downloadedBytes / (double) _gameSizeBytes * 100);
            if (progressPercent <= 100)
            {
                ProgressStatus = $"{progressPercent}%, {speedMBytes}MB/s";
                return;
            }
        }

        speedMBytes = Math.Round((double) stats.Value.downloadSpeedBytes / 1000000, 2);
        var downloadedMBytes = Math.Round(stats.Value.downloadedBytes / 1000000, 2);
        progressPercent = Math.Min(Math.Floor(downloadedMBytes / _game!.GameSize * 97), 100);

        ProgressStatus = $"{progressPercent}%, {speedMBytes}MB/s";
    }

    private void RefreshDownloadStats((double bytesPerSecond, long downloadedBytes, long totalBytes) stats)
    {
        var speedMBytes = Math.Round(stats.bytesPerSecond / 1000000, 2);
        var progressPercent = Math.Floor((double) stats.downloadedBytes / stats.totalBytes * 100);

        ProgressStatus = $"{progressPercent}%, {speedMBytes}MB/s";
    }

    private async Task RunDownloadAndInstallAsync()
    {
        EnsureDeviceConnected(true);
        await DoCancellableAsync(async () => { _path = await DownloadAsync(); }, nameof(Resources.DownloadFailed));

        // successStatus isn't needed here
        await DoCancellableAsync(async () =>
        {
            var deleteAfterInstall =
                _sideloaderSettings.DownloadsPruningPolicy == DownloadsPruningPolicy.DeleteAfterInstall;
            await InstallAsync(_path ?? throw new InvalidOperationException("path is null"),
                deleteAfterInstall);
        }, nameof(Resources.InstallFailed));
    }

    private async Task RunDownloadOnlyAsync()
    {
        await DoCancellableAsync(async () => { _path = await DownloadAsync(); }, nameof(Resources.DownloadFailed),
            nameof(Resources.DownloadSuccess));
    }

    private async Task RunInstallOnlyAsync()
    {
        EnsureDeviceConnected(true);
        // successStatus isn't needed here
        await DoCancellableAsync(async () =>
        {
            _ = _path ?? throw new InvalidOperationException("path is null");
            var deleteAfterInstall = _path.StartsWith(_sideloaderSettings.DownloadsLocation) &&
                                     _sideloaderSettings.DownloadsPruningPolicy ==
                                     DownloadsPruningPolicy.DeleteAfterInstall;
            await InstallAsync(_path, deleteAfterInstall);
        }, nameof(Resources.InstallFailed));
    }

    private async Task RunUninstallAsync()
    {
        EnsureDeviceConnected(true);
        await DoCancellableAsync(async () => { await UninstallAsync(); }, nameof(Resources.UninstallFailed),
            nameof(Resources.UninstallSuccess));
    }

    private async Task RunBackupAndUninstallAsync()
    {
        EnsureDeviceConnected(true);
        await DoCancellableAsync(async () =>
        {
            await BackupAsync();
            await UninstallAsync();
        }, nameof(Resources.UninstallFailed), nameof(Resources.UninstallSuccess));
    }

    private async Task RunBackupAsync()
    {
        EnsureDeviceConnected(true);
        await DoCancellableAsync(async () => { await BackupAsync(); }, nameof(Resources.BackupFailed),
            nameof(Resources.BackupSuccess));
    }

    private async Task RunRestoreAsync()
    {
        EnsureDeviceConnected(true);
        await DoCancellableAsync(async () => { await RestoreAsync(_backup!); }, nameof(Resources.RestoreFailed),
            nameof(Resources.RestoreSuccess));
    }

    private async Task RunPullAndUploadAsync()
    {
        EnsureDeviceConnected();
        await DoCancellableAsync(async () =>
        {
            Status = Resources.PullingFromDevice;
            var path = "";
            await Task.Run(() =>
            {
                path = _adbDevice!.PullApp(_app!.PackageName, "donations", _cancellationTokenSource.Token);
            });
            Status = Resources.PreparingToUpload;
            var apkInfo = await GeneralUtils.GetApkInfoAsync(Path.Combine(path, _app!.PackageName + ".apk"));
            var archiveName =
                GeneralUtils.SanitizeFileName(
                    $"{apkInfo.ApplicationLabel} v{apkInfo.VersionCode} {apkInfo.PackageName}.zip");
            await File.WriteAllTextAsync(Path.Combine(path, "HWID.txt"),
                GeneralUtils.GetHwid(false));
            var archivePath = await ZipUtil.CreateArchiveAsync(path, "donations",
                archiveName, _cancellationTokenSource.Token);
            Directory.Delete(path, true);
            Status = Resources.Uploading;
            await _downloaderService.UploadDonationAsync(archivePath,
                _cancellationTokenSource.Token);
            Globals.MainWindowViewModel!.OnGameDonated(apkInfo.PackageName, apkInfo.VersionCode);
        }, nameof(Resources.DonationFailed), nameof(Resources.UploadSuccess));
    }

    private async Task RunInstallTrailersAddonAsync()
    {
        await DoCancellableAsync(async () =>
        {
            if (Directory.Exists(PathHelper.TrailersPath) && !File.Exists(_path))
            {
                OnFinished(nameof(Resources.AlreadyInstalled));
                return;
            }

            await InstallTrailersAddonAsync();
        }, nameof(Resources.InstallFailed), nameof(Resources.InstallSuccess));
    }

    private async Task RunExtractAsync()
    {
        EnsureDeviceConnected();
        Status = Resources.PullingFromDevice;
        await DoCancellableAsync(
            async () =>
            {
                await Task.Run(() =>
                {
                    _adbDevice!.PullApp(_app!.PackageName, _path!, _cancellationTokenSource.Token);
                });
            }, nameof(Resources.ExtractionFailed), nameof(Resources.ExtractSuccess));
    }

    private async Task RunPullMediaAsync()
    {
        EnsureDeviceConnected();
        Status = Resources.PullingPicturesAndVideos;
        await DoCancellableAsync(
            async () => { await Task.Run(() => { _adbDevice!.PullMedia(_path!, _cancellationTokenSource.Token); }); },
            nameof(Resources.PullPicturesAndVideosFailed), nameof(Resources.PullPicturesAndVideosSuccess));
    }


    private async Task DoCancellableAsync(Func<Task> func, string? failureStatus = null, string? successStatus = null)
    {
        try
        {
            await func();
            if (!string.IsNullOrEmpty(successStatus))
                OnFinished(successStatus);
        }
        catch (OperationCanceledException)
        {
            OnFinished(nameof(Resources.Cancelled));
        }
        catch (Exception e)
        {
            if (!string.IsNullOrEmpty(failureStatus))
                OnFinished(failureStatus, false, e);
        }
    }

    private async Task<string> DownloadAsync()
    {
        var downloadStatsSubscription = Disposable.Empty;
        Status = Resources.DownloadQueued;
        await DownloaderService.TakeDownloadLockAsync(_cancellationTokenSource.Token);
        try
        {
            Status = Resources.CalculatingSize;
            _gameSizeBytes = await _downloaderService.GetGameSizeBytesAsync(_game!, _cancellationTokenSource.Token);
            Status = Resources.Downloading;
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
            ProgressStatus = "";
            downloadStatsSubscription.Dispose();
            DownloaderService.ReleaseDownloadLock();
        }
    }

    private async Task InstallAsync(string gamePath, bool deleteAfterInstall = false)
    {
        Status = Resources.InstallQueued;
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

        using var _ = LogContext.PushProperty("Device", _adbDevice!.ToString());
        Status = Resources.Installing;

        // Here I assume that Install is the last step in the process, this might change in the future
        _adbDevice!.SideloadGame(_game!, gamePath, _cancellationTokenSource.Token)
            .SubscribeOn(RxApp.TaskpoolScheduler)
            .Subscribe(
                x =>
                {
                    Status = x.status;
                    ProgressStatus = x.progress ?? "";
                },
                e =>
                {
                    AdbService.ReleasePackageOperationLock();
                    if (e is OperationCanceledException)
                        OnFinished(nameof(Resources.Cancelled));
                    else
                        OnFinished(nameof(Resources.InstallFailed), false, e);
                },
                () =>
                {
                    ProgressStatus = "";
                    AdbService.ReleasePackageOperationLock();
                    if (deleteAfterInstall && Directory.Exists(gamePath))
                    {
                        Log.Information("Deleting downloaded files from {Path}", gamePath);
                        Status = Resources.DeletingDownloadedFiles;
                        try
                        {
                            Directory.Delete(gamePath, true);
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "Failed to delete downloaded files");
                            // Treating as success because the installation is still successful
                            OnFinished(nameof(Resources.FailedToDeleteDownloadedFiles));
                        }
                    }

                    OnFinished(nameof(Resources.InstallSuccess));
                });
    }

    private async Task UninstallAsync()
    {
        Status = Resources.UninstallQueued;
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            EnsureDeviceConnected();
            using var _ = LogContext.PushProperty("Device", _adbDevice!.ToString());
            Status = Resources.Uninstalling;
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
        Status = Resources.BackupQueued;
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            EnsureDeviceConnected();
            using var _ = LogContext.PushProperty("Device", _adbDevice!.ToString());
            Status = Resources.CreatingBackup;
            await Task.Run(() =>
                _adbDevice!.CreateBackup(_game!.PackageName!, _backupOptions!, _cancellationTokenSource.Token));
        }
        finally
        {
            AdbService.ReleasePackageOperationLock();
        }
    }

    private async Task RestoreAsync(Backup backup)
    {
        Status = Resources.RestoreQueued;
        await AdbService.TakePackageOperationLockAsync(_cancellationTokenSource.Token);
        try
        {
            EnsureDeviceConnected();
            using var _ = LogContext.PushProperty("Device", _adbDevice!.ToString());
            Status = Resources.RestoringBackup;
            await Task.Run(() => _adbDevice!.RestoreBackup(backup));
        }
        finally
        {
            AdbService.ReleasePackageOperationLock();
        }
    }

    private async Task InstallTrailersAddonAsync()
    {
        Status = Resources.Downloading;
        if (!File.Exists(_path))
        {
            var progress =
                new DelegateProgress<(double bytesPerSecond, long downloadedBytes, long totalBytes)>(
                    RefreshDownloadStats);
            _path = await _downloaderService.DownloadTrailersAddon(progress, _cancellationTokenSource.Token);
        }

        Status = Resources.Installing;
        ProgressStatus = "";
        await GeneralUtils.InstallTrailersAddonAsync(_path, true);
        OnFinished(nameof(Resources.InstallSuccess));
    }

    private void OnFinished(string statusResourceNameOrString, bool isSuccess = true, Exception? e = null)
    {
        if (IsFinished)
            return;
        Hint = Resources.ClickToDismiss;
        IsFinished = true;
        Status = Resources.ResourceManager.GetString(statusResourceNameOrString) ?? statusResourceNameOrString;
        ProgressStatus = "";
        Log.Information("Task {TaskId} {TaskType} {TaskName} finished. Result: {Status}. Is success: {IsSuccess}",
            TaskId, _taskType, TaskName,
            Resources.ResourceManager.GetString(statusResourceNameOrString, CultureInfo.InvariantCulture)
            ?? statusResourceNameOrString, isSuccess);
        Globals.MainWindowViewModel!.OnTaskFinished(isSuccess, TaskId);
        if (isSuccess) return;
        if (e is not null)
        {
            Log.Error(e, "Task {TaskName} failed", TaskName);
            Globals.ShowErrorNotification(e, string.Format(Resources.TaskNameFailed, TaskName));
        }
        else
        {
            Log.Error("Task {TaskName} failed", TaskName);
            Globals.ShowNotification(Resources.Error, string.Format(Resources.TaskNameFailed, TaskName),
                NotificationType.Error, TimeSpan.Zero);
        }
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
                 (!simpleCheck && _adbDevice.Ping()))
               )
                return;
        }

        OnFinished(nameof(Resources.FailedNoDeviceConnection), false);
        throw new InvalidOperationException("No device connection");
    }
}