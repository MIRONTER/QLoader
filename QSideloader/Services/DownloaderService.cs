using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.Exceptions;
using FileHelpers;
using Newtonsoft.Json;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.ViewModels;
using Serilog;

namespace QSideloader.Services;

public class DownloaderService
{
    private static readonly SemaphoreSlim MirrorListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim GameListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim DownloadSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim RcloneConfigSemaphoreSlim = new(1, 1);
    private readonly SideloaderSettingsViewModel _sideloaderSettings;

    static DownloaderService()
    {
    }
    private DownloaderService()
    {
        _sideloaderSettings = Globals.SideloaderSettings;
        if (Design.IsDesignMode) return;
        Task.Run(async () =>
        {
            await UpdateRcloneConfigAsync();
            await EnsureGameListAvailableAsync();
            await UpdateResourcesAsync();
        });
    }
    
    public static DownloaderService Instance { get; } = new();
    public List<Game>? AvailableGames { get; private set; }
    public string MirrorName { get; private set; } = "";
    private List<string> MirrorList { get; set; } = new();
    public IEnumerable<string> MirrorListReadOnly => MirrorList.AsReadOnly();

    private bool CanSwitchMirror => RcloneConfigSemaphoreSlim.CurrentCount > 0 &&
                                    MirrorListSemaphoreSlim.CurrentCount > 0 && GameListSemaphoreSlim.CurrentCount > 0;

    private bool IsMirrorListInitialized { get; set; }
    private HttpClient HttpClient { get; } = new();

    private async Task UpdateRcloneConfigAsync()
    {
        await RcloneConfigSemaphoreSlim.WaitAsync();
        try
        {
            Log.Information("Updating rclone config");
            EnsureMirrorSelected();
            while (true)
            {
                if (await TryDownloadConfig())
                {
                    Log.Information("Rclone config updated from {MirrorName}", MirrorName);
                    // Reinitialize the mirror list with the new config and reselect mirror
                    IsMirrorListInitialized = false;
                    MirrorList = new List<string>();
                    EnsureMirrorListInitialized();
                    MirrorName = "";
                    EnsureMirrorSelected();
                    return;
                }

                SwitchMirror();
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to update rclone config");
        }
        finally
        {
            RcloneConfigSemaphoreSlim.Release();
        }

        async Task<bool> TryDownloadConfig()
        {
            try
            {
                var oldConfigPath = Path.Combine(Path.GetDirectoryName(PathHelper.RclonePath)!, "FFA_config");
                var newConfigPath = Path.Combine(Path.GetDirectoryName(PathHelper.RclonePath)!, "FFA_config_new");
                await RcloneTransferAsync("Quest Games/.meta/FFA", newConfigPath, ignoreConfigLock: true,
                    operation: "copyto");
                File.Move(newConfigPath, oldConfigPath, true);
                return true;
            }
            catch (Exception e)
            {
                switch (e)
                {
                    case DownloadQuotaExceededException:
                        Log.Debug("Quota exceeded on rclone config on mirror {MirrorName}",
                            MirrorName);
                        break;
                    case MirrorException:
                        Log.Warning(e, "Error downloading rclone config from mirror {MirrorName} (is mirror down?)",
                            MirrorName);
                        return false;
                    default:
                        throw;
                }
            }

            return false;
        }
    }

    private async Task UpdateResourcesAsync()
    {
        EnsureMirrorSelected();
        Log.Information("Starting resource update in background");

        try
        {
            Log.Debug("Updating thumbnails (async)");
            var tasks = new List<Task>
            {
                RcloneTransferAsync("Quest Games/.meta/thumbnails/", PathHelper.ThumbnailsPath, "sync",
                    retries: 3)
            };
            if (Directory.Exists(PathHelper.TrailersPath))
            {
                Log.Debug("Updating trailers (async)");
                tasks.Add(RcloneTransferAsync("Quest Games/.meta/videos/", PathHelper.TrailersPath, "sync",
                    retries: 3));
            }

            await Task.WhenAll(tasks.ToList());
            Log.Information("Finished resource update");
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to update resources");
        }
    }

    private static List<string> GetMirrorList()
    {
        // regex pattern to parse mirror names from rclone output
        const string mirrorPattern = @"(FFA-\d+):";
        List<string> mirrorList = new();
        var result = Cli.Wrap(PathHelper.RclonePath)
            .WithArguments("listremotes")
            .ExecuteBufferedAsync()
            .GetAwaiter().GetResult();
        var matches = Regex.Matches(result.StandardOutput, mirrorPattern);
        foreach (Match match in matches) mirrorList.Add(match.Groups[1].ToString());

        return mirrorList;
    }

    private void SwitchMirror()
    {
        if (!string.IsNullOrEmpty(MirrorName))
        {
            MirrorListSemaphoreSlim.Wait();
            try
            {
                Log.Information("Excluding mirror {MirrorName} for this session", MirrorName);
                MirrorList.Remove(MirrorName);
                MirrorName = "";
            }
            finally
            {
                MirrorListSemaphoreSlim.Release();
            }
        }

        if (MirrorList.Count == 0)
            throw new DownloaderServiceException("Global mirror list exhausted");
        var random = new Random();
        MirrorName = MirrorList[random.Next(MirrorList.Count)];
        Log.Information("Selected mirror: {MirrorName}", MirrorName);
    }

    public bool TryManualSwitchMirror(string mirrorName)
    {
        if (!MirrorList.Contains(mirrorName))
        {
            Log.Warning("Attempted to switch to unknown mirror {MirrorName}", mirrorName);
            return false;
        }

        if (!CanSwitchMirror)
        {
            Log.Warning("Could not switch to mirror {MirrorName} because of a concurrent operation", mirrorName);
            return false;
        }

        MirrorName = mirrorName;
        Log.Information("Switched to mirror: {MirrorName} (user request)", MirrorName);
        EnsureGameListAvailableAsync(true).GetAwaiter().GetResult();
        return true;
    }

    private void SwitchMirror(IList<string> mirrorList)
    {
        if (!string.IsNullOrEmpty(MirrorName))
        {
            Log.Information("Excluding mirror {MirrorName} for this download", MirrorName);
            mirrorList.Remove(MirrorName);
            MirrorName = "";
        }

        if (mirrorList.Count == 0)
            throw new DownloaderServiceException("Local mirror list exhausted");
        var random = new Random();
        MirrorName = mirrorList[random.Next(mirrorList.Count)];
        Log.Information("Selected mirror: {MirrorName}", MirrorName);
    }

    private void EnsureMirrorSelected()
    {
        MirrorListSemaphoreSlim.Wait();
        try
        {
            EnsureMirrorListInitialized();

            if (!string.IsNullOrEmpty(MirrorName)) return;
            SwitchMirror();
        }
        finally
        {
            MirrorListSemaphoreSlim.Release();
        }
    }

    private void EnsureMirrorListInitialized()
    {
        if (IsMirrorListInitialized) return;
        MirrorList = GetMirrorList();
        Log.Debug("Loaded mirrors: {MirrorList}", MirrorList);
        if (MirrorList.Count == 0)
            throw new DownloaderServiceException("Failed to load mirror list");
        IsMirrorListInitialized = true;
    }

    private async Task RcloneTransferAsync(string source, string destination, string operation,
        string additionalArgs = "", int retries = 1,
        bool ignoreConfigLock = false, CancellationToken ct = default)
    {
        if (!ignoreConfigLock)
            while (RcloneConfigSemaphoreSlim.CurrentCount == 0)
                await Task.Delay(100, ct);
        try
        {
            EnsureMirrorSelected();
            var bwLimit = !string.IsNullOrEmpty(_sideloaderSettings.DownloaderBandwidthLimit)
                ? $"--bwlimit {_sideloaderSettings.DownloaderBandwidthLimit}"
                : "";
            await Cli.Wrap(PathHelper.RclonePath)
                .WithArguments(
                    $"{operation} --retries {retries} {bwLimit} \"{MirrorName}:{source}\" \"{destination}\" {additionalArgs}")
                .ExecuteBufferedAsync(ct);
        }
        catch (Exception e)
        {
            switch (e)
            {
                case OperationCanceledException or TaskCanceledException:
                    throw;
                case CommandExecutionException when e.Message.Contains("downloadQuotaExceeded"):
                    throw new DownloadQuotaExceededException($"Quota exceeded on mirror {MirrorName}", e);
                case CommandExecutionException {ExitCode: 1 or 3 or 4 or 7}:
                    throw new MirrorException($"Rclone {operation} error on mirror {MirrorName}", e);
            }

            throw new DownloaderServiceException($"Error executing rclone {operation}", e);
        }
    }

    // TODO: offline mode
    public async Task EnsureGameListAvailableAsync(bool refresh = false)
    {
        if (AvailableGames is not null && !refresh)
            return;

        await GameListSemaphoreSlim.WaitAsync();

        while (RcloneConfigSemaphoreSlim.CurrentCount == 0)
            await Task.Delay(100);
        try
        {
            EnsureMirrorSelected();

            var csvEngine = new FileHelperEngine<Game>();
            var notesPath = Path.Combine("metadata", "notes.json");
            if (AvailableGames is not null && !refresh)
                return;
            AvailableGames = null;
            Log.Information("Downloading game list");
            while (true)
            {
                var result = await TryDownloadGameListAsync();
                if (result.Success)
                    try
                    {
                        AvailableGames = csvEngine.ReadFile(result.GameListPath).ToList();
                        if (AvailableGames.Count == 0)
                        {
                            Log.Warning("Loaded empty game list from mirror {MirrorName}, retrying", MirrorName);
                            continue;
                        }

                        Log.Information("Loaded {Count} games", AvailableGames.Count);
                        break;
                    }
                    catch (Exception e)
                    {
                        Log.Warning(e, "Failed to read game list from mirror {MirrorName}", MirrorName);
                    }

                SwitchMirror();
            }
            /*if (!Directory.Exists("metadata"))
                Directory.CreateDirectory("metadata");

            if (File.Exists(gameListPath))
            {
                var lastUpdate = File.GetLastWriteTimeUtc(gameListPath);
                gameListRequestMessage.Headers.IfModifiedSince = lastUpdate;
            }

            if (File.Exists(notesPath))
            {
                var lastUpdate = File.GetLastWriteTimeUtc(notesPath);
                notesRequestMessage.Headers.IfModifiedSince = lastUpdate;
            }

            var gameListTask = HttpClient.SendAsync(gameListRequestMessage);
            var notesTask = HttpClient.SendAsync(notesRequestMessage);
            await Task.WhenAll(gameListTask, notesTask);
            var gameListResponse = await gameListTask;
            var notesResponse = await notesTask;

            if (gameListResponse.StatusCode == HttpStatusCode.OK)
            {
                Log.Information("Game list changed on server, updating");
                await using var stream = await gameListResponse.Content.ReadAsStreamAsync();
                await using FileStream outputStream = new(gameListPath, FileMode.Create);
                await stream.CopyToAsync(outputStream);
            }
            else if (gameListResponse.StatusCode != HttpStatusCode.NotModified)
                Log.Error($"Unexpected http response: {gameListResponse.ReasonPhrase}");

            if (notesResponse.StatusCode == HttpStatusCode.OK)
            {
                Log.Information("Notes changed on server, updating");
                await using var stream = await notesResponse.Content.ReadAsStreamAsync();
                await using FileStream outputStream = new(notesPath, FileMode.Create);
                await stream.CopyToAsync(outputStream);
            }
            else if (notesResponse.StatusCode != HttpStatusCode.NotModified)
                Log.Error($"Unexpected http response: {notesResponse.ReasonPhrase}");*/

            if (!File.Exists(notesPath)) return;
            var notesJson = await File.ReadAllTextAsync(notesPath);
            var notesJsonDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(notesJson);
            if (notesJsonDictionary is null) return;
            foreach (var game in AvailableGames)
            {
                if (!notesJsonDictionary.TryGetValue(game.ReleaseName!, out var note)) continue;
                game.Note = note;
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error downloading game list");
        }
        finally
        {
            GameListSemaphoreSlim.Release();
        }

        async Task<(bool Success, string GameListPath)> TryDownloadGameListAsync()
        {
            // Trying different list files seems useless, just wasting time
            //List<string> gameListNames = new(){"FFA.txt", "FFA2.txt", "FFA3.txt", "FFA4.txt"};
            List<string> gameListNames = new(){"FFA.txt"};
            foreach (var gameListName in gameListNames)
                try
                {
                    await RcloneTransferAsync($"Quest Games/{gameListName}", "./metadata/", "copy");
                    var gameListPath = "metadata" + Path.DirectorySeparatorChar + gameListName;
                    return (true, gameListPath);
                }
                catch (Exception e)
                {
                    switch (e)
                    {
                        case DownloadQuotaExceededException:
                            Log.Debug("Quota exceeded on list {GameList} on mirror {MirrorName}",
                                gameListName, MirrorName);
                            break;
                        case MirrorException:
                            Log.Warning(e,
                                "Error downloading list {GameList} from mirror {MirrorName} (is mirror down?)",
                                gameListName, MirrorName);
                            return (false, "");
                        default:
                            throw;
                    }
                }

            //Log.Warning("Quota exceeded on all game lists on mirror {MirrorName}", MirrorName);
            Log.Warning("Quota exceeded on game list on mirror {MirrorName}", MirrorName);
            return (false, "");
        }
    }

    public async Task<string> DownloadGameAsync(Game game, CancellationToken ct = default)
    {
        var stopWatch = Stopwatch.StartNew();
        var srcPath = $"Quest Games/{game.ReleaseName}";
        var dstPath = Path.Combine(_sideloaderSettings.DownloadsLocation, game.ReleaseName!);
        try
        {
            Log.Information("Downloading release {ReleaseName}", game.ReleaseName);
            var localMirrorList = MirrorList.ToList();
            while (true)
                try
                {
                    await RcloneTransferAsync(srcPath, dstPath,
                        "copy",
                        "--progress --drive-acknowledge-abuse --rc --rc-addr :48040 --drive-stop-on-download-limit", 3,
                        ct: ct);
                    var json = JsonConvert.SerializeObject(game);
                    await File.WriteAllTextAsync(Path.Combine(dstPath, "release.json"), json, ct);
                    break;
                }
                catch (Exception e)
                {
                    switch (e)
                    {
                        case DownloadQuotaExceededException:
                            Log.Warning("Quota exceeded on mirror {MirrorName}", MirrorName);
                            break;
                        case MirrorException:
                            Log.Warning(e, "Download error on mirror {MirrorName}", MirrorName);
                            break;
                        default:
                            throw;
                    }

                    SwitchMirror(localMirrorList);
                    Log.Information("Retrying download");
                }

            Log.Debug("Rclone download took {TotalSeconds} seconds", Math.Round(stopWatch.Elapsed.TotalSeconds, 2));
            Log.Information("Release {ReleaseName} downloaded", game.ReleaseName);
            return dstPath;
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException or TaskCanceledException)
                throw;
            Log.Error(e, "Error downloading release");
            throw new DownloaderServiceException("Error downloading release", e);
        }
        finally
        {
            stopWatch.Stop();
        }
    }

    public static async Task TakeDownloadLockAsync(CancellationToken ct = default)
    {
        await DownloadSemaphoreSlim.WaitAsync(ct);
    }

    public static void ReleaseDownloadLock()
    {
        if (DownloadSemaphoreSlim.CurrentCount < 1)
            DownloadSemaphoreSlim.Release();
        else
            Log.Warning("Attempted double release of download lock");
    }

    public IObservable<(float downloadSpeedBytes, double downloadedBytes)?> PollStats(TimeSpan interval, IScheduler scheduler)
    {
        return Observable.Create<(float downloadSpeedBytes, double downloadedBytes)?>(observer =>
        {
            return scheduler.ScheduleAsync(async (ctrl, ct) =>
            {
                while (!ct.IsCancellationRequested)
                {
                    var result = await GetRcloneDownloadStats();
                    observer.OnNext(result);
                    await ctrl.Sleep(interval, ct);
                }
            });
        });
    }

    private async Task<(float downloadSpeedBytes, double downloadedBytes)?> GetRcloneDownloadStats()
    {
        try
        {
            var response = await HttpClient.PostAsync("http://127.0.0.1:48040/core/stats", null);
            var responseContent = await response.Content.ReadAsStringAsync();
            var results = JsonConvert.DeserializeObject<dynamic>(responseContent);
            if (results is null || results["transferring"] is null) return null;
            float downloadSpeedBytes = results.speed.ToObject<float>();
            double downloadedBytes = results.bytes.ToObject<double>();
            return (downloadSpeedBytes, downloadedBytes);
        }
        catch
        {
            return null;
        }
    }
}

public class DownloaderServiceException : Exception
{
    public DownloaderServiceException(string message)
        : base(message)
    {
    }

    public DownloaderServiceException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public class DownloadQuotaExceededException : DownloaderServiceException
{
    public DownloadQuotaExceededException(string message)
        : base(message)
    {
    }

    public DownloadQuotaExceededException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public class MirrorException : DownloaderServiceException
{
    public MirrorException(string message)
        : base(message)
    {
    }

    public MirrorException(string message, Exception inner)
        : base(message, inner)
    {
    }
}