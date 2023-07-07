namespace QSideloader.Models;

public class TaskOptions
{
    public TaskType Type { get; init; }
    public Game? Game { get; init; }
    public string? Path { get; init; }
    public InstalledApp? App { get; init; }
    public BackupOptions? BackupOptions { get; init; }
    public Backup? Backup { get; init; }
}