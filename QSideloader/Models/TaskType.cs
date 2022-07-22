namespace QSideloader.Models;

public enum TaskType
{
    DownloadAndInstall,
    DownloadOnly,
    InstallOnly,
    Uninstall,
    BackupAndUninstall,
    Backup,
    Restore,
    PullAndUpload,
    DownloadAndInstallTrailersAddon
}