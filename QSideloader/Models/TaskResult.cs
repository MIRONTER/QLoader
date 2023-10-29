using System;
using QSideloader.Properties;

namespace QSideloader.Models;

public enum TaskResult
{
    Cancelled,
    InstallFailed,
    AlreadyInstalled,
    OsVersionTooOld,
    InstallSuccess,
    DownloadCleanupFailed,
    DownloadFailed,
    DownloadSuccess,
    NoDeviceConnection,
    UninstallFailed,
    UninstallSuccess,
    BackupFailed,
    BackupSuccess,
    RestoreFailed,
    RestoreSuccess,
    DonationFailed,
    DonationSuccess,
    ExtractionFailed,
    ExtractionSuccess,
    PullMediaFailed,
    PullMediaSuccess,
    UnknownError
}

public static class TaskResultExtensions
{
    public static bool IsSuccess(this TaskResult result)
    {
        return result switch
        {
            TaskResult.Cancelled => true,
            TaskResult.InstallFailed => false,
            TaskResult.AlreadyInstalled => false,
            TaskResult.OsVersionTooOld => false,
            TaskResult.InstallSuccess => true,
            TaskResult.DownloadCleanupFailed => false,
            TaskResult.DownloadFailed => false,
            TaskResult.DownloadSuccess => true,
            TaskResult.NoDeviceConnection => false,
            TaskResult.UninstallFailed => false,
            TaskResult.UninstallSuccess => true,
            TaskResult.BackupFailed => false,
            TaskResult.BackupSuccess => true,
            TaskResult.RestoreFailed => false,
            TaskResult.RestoreSuccess => true,
            TaskResult.DonationFailed => false,
            TaskResult.DonationSuccess => true,
            TaskResult.ExtractionFailed => false,
            TaskResult.ExtractionSuccess => true,
            TaskResult.PullMediaFailed => false,
            TaskResult.PullMediaSuccess => true,
            TaskResult.UnknownError => false,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null)
        };
    }

    public static string GetMessage(this TaskResult result)
    {
        return result switch
        {
            TaskResult.Cancelled => Resources.Cancelled,
            TaskResult.InstallFailed => Resources.TaskResultInstallFailed,
            TaskResult.AlreadyInstalled => Resources.TaskResultAlreadyInstalled,
            TaskResult.OsVersionTooOld => Resources.TaskResultOsVersionTooOld,
            TaskResult.InstallSuccess => Resources.TaskResultInstallSuccess,
            TaskResult.DownloadCleanupFailed => Resources.TaskResultFailedToDeleteDownloadedFiles,
            TaskResult.DownloadFailed => Resources.TaskResultDownloadFailed,
            TaskResult.DownloadSuccess => Resources.TaskResultDownloadSuccess,
            TaskResult.NoDeviceConnection => Resources.TaskResultNoDeviceConnection,
            TaskResult.UninstallFailed => Resources.TaskResultUninstallFailed,
            TaskResult.UninstallSuccess => Resources.TaskResultUninstallSuccess,
            TaskResult.BackupFailed => Resources.TaskResultBackupFailed,
            TaskResult.BackupSuccess => Resources.TaskResultBackupSuccess,
            TaskResult.RestoreFailed => Resources.TaskResultRestoreFailed,
            TaskResult.RestoreSuccess => Resources.TaskResultRestoreSuccess,
            TaskResult.DonationFailed => Resources.TaskResultDonationFailed,
            TaskResult.DonationSuccess => Resources.TaskResultUploadSuccess,
            TaskResult.ExtractionFailed => Resources.TaskResultExtractFailed,
            TaskResult.ExtractionSuccess => Resources.TaskResultExtractSuccess,
            TaskResult.PullMediaFailed => Resources.TaskResultPullMediaFailed,
            TaskResult.PullMediaSuccess => Resources.TaskResultPullMediaSuccess,
            TaskResult.UnknownError => Resources.TaskResultUnknownError,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null)
        };
    }
}