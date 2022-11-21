namespace QSideloader.Models;

/// <summary>
/// Options for game backups.
/// </summary>
public class BackupOptions
{
    /// <summary>
    /// String to append to backup name.
    /// </summary>
    public string? NameAppend { get; set; }

    /// <summary>
    /// Should backup APK.
    /// </summary>
    public bool BackupApk { get; set; }

    /// <summary>
    /// Should backup data.
    /// </summary>
    public bool BackupData { get; set; } = true;

    /// <summary>
    /// Should backup OBB files.
    /// </summary>
    public bool BackupObb { get; set; }
}