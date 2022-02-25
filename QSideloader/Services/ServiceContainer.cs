namespace QSideloader.Services;

public static class ServiceContainer
{
    public static DownloaderService DownloaderService { get; } = new();
    public static ADBService ADBService { get; } = new();
}