namespace QSideloader.Models;

/// <summary>
/// Options for game backups.
/// </summary>
public class BackupOptions
{
    /// <summary>
    /// String to append to backup name.
    /// </summary>
    public string? NameAppend { get; init; }

    /// <summary>
    /// Should backup APK.
    /// </summary>
    public bool BackupApk { get; init; }

    /// <summary>
    /// Should backup data.
    /// </summary>
    public bool BackupData { get; init; } = true;

    /// <summary>
    /// Should backup OBB files.
    /// </summary>
    public bool BackupObb { get; init; }
}