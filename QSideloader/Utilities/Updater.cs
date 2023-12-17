using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using QSideloader.Common;
using QSideloader.Services;
using Serilog;

namespace QSideloader.Utilities;

public class Updater
{
    private UpdateInfo UpdateInfo { get; set; } = new();
    public VersionItem? UpdateVersion { get; private set; }
    private bool IsUpdateAvailable { get; set; }
    private UpdateAsset? UpdaterAsset { get; set; }
    
    public async Task<bool> CheckForUpdatesAsync()
    {
        if (Globals.Overrides.TryGetValue("DisableSelfUpdate", out var value) &&
            value == "1")
        {
            Log.Warning("Updater disabled by override");
            return false;
        }
        Log.Debug("Checking for updates");
        var executingAssembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = executingAssembly.GetName().Version;
        if (Globals.Overrides.TryGetValue("UpdateInfoUrl", out var updateInfoUrl) && !string.IsNullOrWhiteSpace(updateInfoUrl))
            Log.Debug("Using update info url override: {Url}", updateInfoUrl);
        UpdateInfo = await UpdateInfo.GetInfoAsync(updateInfoUrl);
        UpdateVersion = UpdateInfo.GetLatestVersion(version);
        IsUpdateAvailable = UpdateVersion is not null;
        if (UpdateVersion is null)
        {
            Log.Debug("No updates available");
            UpdaterAsset = null;
            return false;
        }
        Log.Information("Update available. Latest version: {Version}", UpdateVersion.Version);
        UpdaterAsset = UpdateInfo.GetUpdaterAsset();
        return IsUpdateAvailable;
    }
    
    

    public async Task DownloadAndExecuteUpdaterAsync()
    {
        if (UpdaterAsset is null)
        {
            Log.Error("Updater asset is null, cannot download updater");
            Globals.ShowNotification("Error", "Updater asset is null, cannot download updater",
                NotificationType.Error);
            return;
        }

        var updaterFileName = OperatingSystem.IsWindows() ? "Updater.exe" : "Updater";
        var updaterUrl = UpdaterAsset.Url;
        var updaterSha256 = UpdaterAsset.Sha256;

        try
        {
            await DownloaderService.DownloadUpdaterAsync(updaterUrl, updaterSha256, updaterFileName);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to download updater");
            Globals.ShowErrorNotification(e, "Failed to download updater");
            return;
        }

        var process = new Process
        {
            StartInfo =
            {
                FileName = updaterFileName,
                Arguments = "--channel stable",
                UseShellExecute = true
            }
        };
        process.Start();
        Log.Information("Executed updater, application will now exit");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime app)
            {
                app.Shutdown();
                // If the application doesn't exit in 5 seconds, force exit
                Observable.Timer(TimeSpan.FromSeconds(5)).Subscribe(_ => Environment.Exit(0));
            }
            else
            {
                Environment.Exit(0);
            }
        });
    }
}