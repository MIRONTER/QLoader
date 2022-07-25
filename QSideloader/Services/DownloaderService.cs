using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.Exceptions;
using FileHelpers;
using Newtonsoft.Json;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.ViewModels;
using Serilog;
using SerilogTimings;

namespace QSideloader.Services;

public class DownloaderService
{
    private static readonly SemaphoreSlim MirrorListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim GameListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim DownloadSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim RcloneConfigSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim TrailersAddonSemaphoreSlim = new(1, 1);
    private readonly SideloaderSettingsViewModel _sideloaderSettings;

    static DownloaderService()
    {
        var appVersion = Assembly.GetExecutingAssembly().GetName().Version;
        var appVersionString = appVersion is not null
            ? $"{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}"
            : "Unknown";
        ApiHttpClient.DefaultRequestHeaders.UserAgent.Add(
            new System.Net.Http.Headers.ProductInfoHeaderValue("QLoader", appVersionString));
    }
    private DownloaderService()
    {
        _sideloaderSettings = Globals.SideloaderSettings;
        if (Design.IsDesignMode) return;
        Task.Run(async () =>
        {
            await UpdateRcloneConfigAsync();
            await EnsureMetadataAvailableAsync();
            await UpdateResourcesAsync();
        });
    }
    
    public static DownloaderService Instance { get; } = new();
    public List<Game>? AvailableGames { get; private set; }
    public List<string> DonationBlacklistedPackages { get; private set; } = new();
    public string MirrorName { get; private set; } = "";
    private List<string> MirrorList { get; set; } = new();
    private List<string> ExcludedMirrorList { get; set; } = new();
    public IEnumerable<string> MirrorListReadOnly => MirrorList.AsReadOnly();
    public static int RcloneStatsPort => 48040;

    private bool CanSwitchMirror => RcloneConfigSemaphoreSlim.CurrentCount > 0 &&
                                    MirrorListSemaphoreSlim.CurrentCount > 0 && GameListSemaphoreSlim.CurrentCount > 0;

    private bool IsMirrorListInitialized { get; set; }
    private HttpClient HttpClient { get; } = new();
    private static HttpClient ApiHttpClient { get; } = new() {BaseAddress = new Uri("https://qloader.5698452.xyz/api/v1/")};

    private async Task UpdateRcloneConfigAsync()
    {
        await RcloneConfigSemaphoreSlim.WaitAsync();
        try
        {
            Log.Information("Updating rclone config");
            EnsureMirrorSelected();
            while (true)
            {
                if (await TryDownloadConfigAsync())
                {
                    Log.Information("Rclone config updated from {MirrorName}", MirrorName);
                    ReloadMirrorList();
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

        async Task<bool> TryDownloadConfigAsync()
        {
            try
            {
                var oldConfigPath = Path.Combine(Path.GetDirectoryName(PathHelper.RclonePath)!, "FFA_config");
                var newConfigPath = Path.Combine(Path.GetDirectoryName(PathHelper.RclonePath)!, "FFA_config_new");
                await RcloneTransferInternalAsync($"{MirrorName}:Quest Games/.meta/FFA", newConfigPath, operation: "copyto");
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
                    case RcloneTransferException:
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

    private void ExcludeDeadMirrors()
    {
        var list = ApiHttpClient.GetFromJsonAsync<List<Dictionary<string, JsonElement>>>("mirrors?status=DOWN")
            .GetAwaiter().GetResult();
        if (list is null) return;
        var count = 0;
        foreach (var mirrorName in list.Select(mirror => mirror["mirror_name"].GetString())
                     .Where(mirrorName => mirrorName is not null && !ExcludedMirrorList.Contains(mirrorName)))
        {
            ExcludedMirrorList.Add(mirrorName!);
            count++;
        }

        if (count > 0)
            Log.Information("Excluded {Count} dead mirrors for this session", count);
    }

    public void ReloadMirrorList()
    {
        // Reinitialize the mirror list with the new config and reselect mirror, if needed
        using var op = Operation.Time("Reloading mirror list");
        IsMirrorListInitialized = false;
        MirrorList = new List<string>();
        ExcludedMirrorList = new List<string>();
        EnsureMirrorListInitialized();
        if (MirrorList.Contains(MirrorName)) return;
        Log.Information("Mirror {MirrorName} not found in new mirror list", MirrorName);
        MirrorName = "";
        EnsureMirrorSelected();
    }

    private void SwitchMirror()
    {
        if (!string.IsNullOrEmpty(MirrorName))
        {
            MirrorListSemaphoreSlim.Wait();
            try
            {
                Log.Information("Excluding mirror {MirrorName} for this session", MirrorName);
                ExcludedMirrorList.Add(MirrorName);
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
        EnsureMetadataAvailableAsync(true).GetAwaiter().GetResult();
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
        ExcludeDeadMirrors();
        MirrorList = GetMirrorList().Where(x => !ExcludedMirrorList.Contains(x)).ToList();
        Log.Debug("Loaded mirrors: {MirrorList}", MirrorList);
        if (MirrorList.Count == 0)
            throw new DownloaderServiceException("Failed to load mirror list");
        IsMirrorListInitialized = true;
    }

    private async Task RcloneTransferAsync(string source, string destination, string operation,
        string additionalArgs = "", int retries = 1, CancellationToken ct = default)
    {
        await RcloneConfigSemaphoreSlim.WaitAsync(ct);
        RcloneConfigSemaphoreSlim.Release();
        EnsureMirrorSelected();
        source = $"{MirrorName}:{source}";
        await RcloneTransferInternalAsync(source, destination, operation, additionalArgs, retries, ct);
    }

    private async Task RcloneTransferInternalAsync(string source, string destination, string operation,
        string additionalArgs = "", int retries = 1, CancellationToken ct = default)
    {
        using var op = Operation.Begin("Rclone {Operation} \"{Source}\" -> \"{Destination}\"", operation, source,
            destination);
        try
        {
            var bwLimit = !string.IsNullOrEmpty(_sideloaderSettings.DownloaderBandwidthLimit)
                ? $"--bwlimit {_sideloaderSettings.DownloaderBandwidthLimit}"
                : "";
            await Cli.Wrap(PathHelper.RclonePath)
                .WithArguments(
                    $"{operation} --retries {retries} {bwLimit} \"{source}\" \"{destination}\" {additionalArgs}")
                .ExecuteBufferedAsync(ct);
            op.Complete();
        }
        catch (Exception e)
        {
            switch (e)
            {
                case OperationCanceledException:
                    throw;
                case CommandExecutionException when e.Message.Contains("downloadQuotaExceeded"):
                    throw new DownloadQuotaExceededException(MirrorName, source, e);
                case CommandExecutionException {ExitCode: 1 or 3 or 4 or 7}:
                    if (!e.Message.Contains("no such host"))
                        throw new RcloneTransferException($"Rclone {operation} error on mirror {MirrorName}", e);
                    break;
            }

            throw new DownloaderServiceException($"Error executing rclone {operation}", e);
        }
    }

    // TODO: offline mode
    public async Task EnsureMetadataAvailableAsync(bool refresh = false)
    {
        var skip = AvailableGames is not null && !refresh || Design.IsDesignMode;

        await GameListSemaphoreSlim.WaitAsync();
        if (skip)
        {
            GameListSemaphoreSlim.Release();
            return;
        }

        while (RcloneConfigSemaphoreSlim.CurrentCount == 0)
            await Task.Delay(100);
        try
        {
            EnsureMirrorSelected();

            var csvEngine = new FileHelperEngine<Game>();
            if (AvailableGames is not null && !refresh)
                return;
            AvailableGames = null;
            Log.Information("Downloading game list");
            while (true)
            {
                if (await TryDownloadGameListAsync())
                    try
                    {
                        AvailableGames = csvEngine.ReadFile(Path.Combine("metadata", "FFA.txt")).ToList();
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
            
            await TryLoadPopularityAsync();

            await TryLoadDonationBlacklistAsync();
            

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
                Log.Error($"Unexpected http response: {notesResponse.ReasonPhrase}");

            if (!File.Exists(notesPath)) return;
            var notesJson = await File.ReadAllTextAsync(notesPath);
            var notesJsonDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(notesJson);
            if (notesJsonDictionary is null) return;
            foreach (var game in AvailableGames)
            {
                if (!notesJsonDictionary.TryGetValue(game.ReleaseName!, out var note)) continue;
                game.Note = note;
            }*/
        }
        catch (Exception e)
        {
            Log.Error(e, "Error downloading game list");
            Globals.ShowErrorNotification(e, "Error downloading game list");
        }
        finally
        {
            GameListSemaphoreSlim.Release();
        }

        async Task<bool> TryDownloadGameListAsync()
        {
            // Trying different list files seems useless, just wasting time
            //List<string> gameListNames = new(){"FFA.txt", "FFA2.txt", "FFA3.txt", "FFA4.txt"};
            List<string> gameListNames = new(){"FFA.txt"};
            foreach (var gameListName in gameListNames)
                try
                {
                    await RcloneTransferAsync($"Quest Games/{gameListName}", "./metadata/FFA_new.txt", "copyto");
                    File.Move("./metadata/FFA_new.txt", "./metadata/FFA.txt", true);
                    return true;
                }
                catch (Exception e)
                {
                    switch (e)
                    {
                        case DownloadQuotaExceededException:
                            Log.Debug("Quota exceeded on list {GameList} on mirror {MirrorName}",
                                gameListName, MirrorName);
                            break;
                        case RcloneTransferException:
                            Log.Warning(e,
                                "Error downloading list {GameList} from mirror {MirrorName} (is mirror down?)",
                                gameListName, MirrorName);
                            return false;
                        default:
                            throw;
                    }
                }

            //Log.Warning("Quota exceeded on all game lists on mirror {MirrorName}", MirrorName);
            Log.Warning("Quota exceeded on game list on mirror {MirrorName}", MirrorName);
            return false;
        }

        async Task TryLoadPopularityAsync()
        {
            try
            {
                List<Dictionary<string, JsonElement>>? popularity;
                using (var _ = Operation.Time("Requesting popularity from API"))
                {
                    popularity =
                        await ApiHttpClient.GetFromJsonAsync<List<Dictionary<string, JsonElement>>>("popularity");
                }
                if (popularity is not null)
                {
                    var popularityMax1D = popularity.Max(x => x["1D"].GetInt32());
                    var popularityMax7D = popularity.Max(x => x["7D"].GetInt32());
                    var popularityMax30D = popularity.Max(x => x["30D"].GetInt32());
                    foreach (var game in AvailableGames)
                    {
                        var popularityEntry =
                            popularity.FirstOrDefault(x => x["package_name"].GetString() == game.PackageName);
                        if (popularityEntry is null) continue;
                        game.Popularity["1D"] =
                            (int) Math.Round(popularityEntry["1D"].GetInt32() / (double) popularityMax1D * 100);
                        game.Popularity["7D"] =
                            (int) Math.Round(popularityEntry["7D"].GetInt32() / (double) popularityMax7D * 100);
                        game.Popularity["30D"] =
                            (int) Math.Round(popularityEntry["30D"].GetInt32() / (double) popularityMax30D * 100);
                    }
                    Log.Information("Loaded popularity data");
                    return;
                }

                Log.Error("Failed to load popularity data");
                Globals.ShowNotification("Error", "Failed to load popularity data", NotificationType.Warning,
                    TimeSpan.FromSeconds(5));
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to load popularity data");
                Globals.ShowErrorNotification(e, "Failed to load popularity data", NotificationType.Warning,
                    TimeSpan.FromSeconds(5));
            }
        }
        
        async Task TryLoadDonationBlacklistAsync()
        {
            try
            {
                await RcloneTransferAsync("Quest Games/.meta/nouns/blacklist.txt", "./metadata/blacklist_new.txt", "copyto");
                File.Move(Path.Combine("metadata", "blacklist_new.txt"), Path.Combine("metadata", "blacklist.txt"), true);
                Log.Debug("Downloaded donation blacklist");
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to download donation blacklist");
            }
            try
            {
                if (File.Exists(Path.Combine("metadata", "blacklist.txt")))
                    DonationBlacklistedPackages =
                        (await File.ReadAllLinesAsync(Path.Combine("metadata", "blacklist.txt"))).ToList();
                Log.Debug("Loaded {BlacklistedPackagesCount} blacklisted packages", DonationBlacklistedPackages.Count);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to load donation blacklist");
            }
        }
    }

    public async Task<string> DownloadGameAsync(Game game, CancellationToken ct = default)
    {
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
                        $"--progress --drive-acknowledge-abuse --rc --rc-addr :{RcloneStatsPort} --drive-stop-on-download-limit",
                        3, ct: ct);
                    var json = JsonConvert.SerializeObject(game, Formatting.Indented);
                    await File.WriteAllTextAsync(Path.Combine(dstPath, "release.json"), json, ct);
#pragma warning disable CS4014
                    Task.Run(async () => await ReportGameDownload(game.PackageName!), CancellationToken.None);
#pragma warning restore CS4014
                    break;
                }
                catch (Exception e)
                {
                    switch (e)
                    {
                        case DownloadQuotaExceededException:
                            Log.Warning("Quota exceeded on mirror {MirrorName}", MirrorName);
                            break;
                        case RcloneTransferException:
                            Log.Warning(e, "Download error on mirror {MirrorName}", MirrorName);
                            break;
                        default:
                            throw;
                    }

                    SwitchMirror(localMirrorList);
                    Log.Information("Retrying download");
                }

            Log.Information("Release {ReleaseName} downloaded", game.ReleaseName);
            return dstPath;
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException)
                throw;
            Log.Error(e, "Error downloading release");
            throw new DownloaderServiceException("Error downloading release", e);
        }
    }
    
    private async Task ReportGameDownload(string packageName)
    {
        using var op = Operation.Begin("Reporting game {PackageName} download to API", packageName);
        try
        {
            var dict = new Dictionary<string, string> {{"hwid", GeneralUtils.GetHwid()}, {"package_name", packageName}};
            var json = JsonConvert.SerializeObject(dict, Formatting.None);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await ApiHttpClient.PostAsync("download", content);
            response.EnsureSuccessStatusCode();
            op.Complete();
        }
        catch (Exception e)
        {
            op.SetException(e);
            op.Abandon();
        }
    }

    public static async Task TakeDownloadLockAsync(CancellationToken ct = default)
    {
        await DownloadSemaphoreSlim.WaitAsync(ct);
    }

    public static void ReleaseDownloadLock()
    {
        DownloadSemaphoreSlim.Release();
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

    public async Task UploadDonationAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Archive not found", path);
        using var op = Operation.Begin("Uploading donation");
        var archiveName = Path.GetFileName(path);
        if (!Regex.IsMatch(archiveName, @"^.+ v\d+ .+\.zip$"))
            throw new ArgumentException("Invalid archive name", nameof(path));
        Log.Information("Uploading donation {ArchiveName}", archiveName);
        var md5Sum = GeneralUtils.GetFileChecksum(HashingAlgoTypes.MD5, path).ToLower();
        await File.WriteAllTextAsync(path + ".md5sum", md5Sum, ct);
        await RcloneConfigSemaphoreSlim.WaitAsync(ct);
        RcloneConfigSemaphoreSlim.Release();
        await RcloneTransferInternalAsync(path, "FFA-DD:/_donations/", "copy", ct: ct);
        await RcloneTransferInternalAsync(path + ".md5sum", "FFA-DD:/md5sum/", "copy",
            ct: ct);
        op.Complete();
    }

    public async Task<string> DownloadTrailersAddon(CancellationToken ct = default)
    {
        if (TrailersAddonSemaphoreSlim.CurrentCount == 0)
            throw new DownloaderServiceException("Trailers addon is already downloading");
        await TrailersAddonSemaphoreSlim.WaitAsync(ct);
        using var op = Operation.Begin("Downloading trailers addon");
        var trailersAddonPath = Path.Combine(_sideloaderSettings.DownloadsLocation, "TrailersAddon.zip");
        try
        {
            const string trailersAddonUrl =
                "https://github.com/skrimix/QLoaderFiles/releases/latest/download/TrailersAddon.zip";
            Log.Information("Downloading trailers addon from {TrailersAddonUrl}", trailersAddonUrl);
            if (File.Exists(trailersAddonPath))
                File.Delete(trailersAddonPath);
            await using var stream = await HttpClient.GetStreamAsync(trailersAddonUrl, ct);
            await using (var fileStream = new FileStream(trailersAddonPath, FileMode.CreateNew))
            {
                await stream.CopyToAsync(fileStream, ct);
                await fileStream.FlushAsync(ct);
            }

            op.Complete();
            return trailersAddonPath;
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException)
            {
                if (File.Exists(trailersAddonPath))
                    File.Delete(trailersAddonPath);
                op.Cancel();
                throw;
            }
            op.SetException(e);
            op.Abandon();
            throw;
        }
        finally
        {
            TrailersAddonSemaphoreSlim.Release();
        }
    }
    
    public async Task<OculusGame?> GetGameStoreInfo(string? packageName)
    {
        if (packageName is null)
            return null;
        using var op = Operation.Begin("Getting game store info for {PackageName}", packageName);
        var uri = $"oculusgames/{packageName}";
        var response = await ApiHttpClient.GetAsync(uri);
        var responseContent = await response.Content.ReadAsStringAsync();
        // If response is empty dictionary, the api didn't find the game
        if (responseContent == "{}")
        {
            Log.Information("Game store info not found for {PackageName}", packageName);
            op.Complete();
            return null;
        }
        var game = JsonConvert.DeserializeObject<OculusGame>(responseContent);
        op.Complete();
        return game;
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
    public string MirrorName { get; }
    public string RemotePath { get; }
    public DownloadQuotaExceededException(string mirrorName, string remotePath)
        : base($"Quota exceeded on mirror {mirrorName} for path {remotePath}")
    {
        MirrorName = mirrorName;
        RemotePath = remotePath;
    }

    public DownloadQuotaExceededException(string mirrorName, string remotePath, Exception inner)
        : base($"Quota exceeded on mirror {mirrorName}", inner)
    {
        MirrorName = mirrorName;
        RemotePath = remotePath;
    }
}

public class RcloneTransferException : DownloaderServiceException
{
    public RcloneTransferException(string message)
        : base(message)
    {
    }

    public RcloneTransferException(string message, Exception inner)
        : base(message, inner)
    {
    }
}