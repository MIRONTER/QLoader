using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.Exceptions;
using Downloader;
using FileHelpers;
using Newtonsoft.Json;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Utilities;
using QSideloader.ViewModels;
using Serilog;
using Serilog.Events;
using SerilogTimings;

namespace QSideloader.Services;

/// <summary>
///     Service for all download/upload operations and API calls.
/// </summary>
public class DownloaderService
{
    private const string ApiUrl = "https://qloader.5698452.xyz/api/v1/";
    private static readonly SemaphoreSlim MirrorListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim GameListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim DownloadSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim RcloneConfigSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim TrailersAddonSemaphoreSlim = new(1, 1);
    private readonly SideloaderSettingsViewModel _sideloaderSettings;
    private List<string> _mirrorList = new();

    static DownloaderService()
    {
        var httpClientHandler = new HttpClientHandler
        {
            Proxy = WebRequest.DefaultWebProxy
        };
        ApiHttpClient = new HttpClient(httpClientHandler) {BaseAddress = new Uri(ApiUrl)};
        var appVersion = Assembly.GetExecutingAssembly().GetName().Version;
        var appVersionString = appVersion is not null
            ? $"{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}"
            : "Unknown";
        ApiHttpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("QLoader", appVersionString));
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

    private List<string> ExcludedMirrorList { get; set; } = new();
    public IReadOnlyList<string> MirrorList => _mirrorList.AsReadOnly();
    public static int RcloneStatsPort => 48040;

    private bool CanSwitchMirror => RcloneConfigSemaphoreSlim.CurrentCount > 0 &&
                                    MirrorListSemaphoreSlim.CurrentCount > 0 && GameListSemaphoreSlim.CurrentCount > 0;

    private bool IsMirrorListInitialized { get; set; }
    private static HttpClient HttpClient { get; } = new();
    private static HttpClient ApiHttpClient { get; }

    /// <summary>
    ///     Downloads a new config file for rclone from a mirror.
    /// </summary>
    private async Task UpdateRcloneConfigAsync()
    {
        await RcloneConfigSemaphoreSlim.WaitAsync();
        try
        {
            EnsureMirrorSelected();
            if (MirrorListContainsVip(MirrorList))
                return;
            Log.Information("Updating rclone config");
            while (true)
            {
                if (await TryDownloadConfigAsync())
                {
                    Log.Information("Rclone config updated from {MirrorName}", MirrorName);
                    ReloadMirrorList(true);
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
                await RcloneTransferInternalAsync($"{MirrorName}:Quest Games/.meta/FFA", newConfigPath,
                    "copyto");
                File.Move(newConfigPath, oldConfigPath, true);
                return true;
            }
            catch (DownloadQuotaExceededException)
            {
                Log.Debug("Quota exceeded on rclone config on mirror {MirrorName}",
                    MirrorName);
            }
            catch (RcloneOperationException e)
            {
                Log.Warning(e, "Error downloading rclone config from mirror {MirrorName} (is mirror down?)",
                    MirrorName);
            }

            return false;
        }
    }

    /// <summary>
    ///     Updates resources (thumbnails, videos).
    /// </summary>
    private async Task UpdateResourcesAsync()
    {
        EnsureMirrorSelected();
        Log.Information("Starting resource update in background");

        try
        {
            var tasks = new List<Task>();
            if (Directory.Exists(PathHelper.ThumbnailsPath))
            {
                Log.Debug("Updating thumbnails (async)");
                tasks.Add(RcloneTransferAsync("Quest Games/.meta/thumbnails/", PathHelper.ThumbnailsPath, "sync",
                    retries: 3));
            }
            if (Directory.Exists(PathHelper.TrailersPath))
            {
                Log.Debug("Updating trailers (async)");
                tasks.Add(RcloneTransferAsync("Quest Games/.meta/videos/", PathHelper.TrailersPath, "sync",
                    retries: 3));
            }

            await Task.WhenAll(tasks);
            Log.Information("Finished resource update");
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to update resources");
        }
    }

    /// <summary>
    ///     Gets the list of rclone mirrors.
    /// </summary>
    /// <returns><see cref="List{T}" /> of mirrors.</returns>
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
        if (!MirrorListContainsVip(mirrorList)) return mirrorList;
        if (CheckVipAccess())
        {
            Log.Information("Verified VIP access");
            return mirrorList;
        }
        Globals.ShowNotification("Error", Resources.CouldntVerifyVip, NotificationType.Error, TimeSpan.Zero);
        throw new DownloaderServiceException("Couldn't verify VIP access");
    }

    private static bool MirrorListContainsVip(IEnumerable<string> mirrorList)
    {
        return mirrorList.Any(x => Regex.IsMatch(x, @"FFA-9."));
    }

    private static bool CheckVipAccess()
    {
        var registeredHwidsResponse = HttpClient.GetAsync("https://github.com/harryeffinpotter/-Loader/raw/main/HWIDS")
            .GetAwaiter().GetResult();
        if (!registeredHwidsResponse.IsSuccessStatusCode)
        {
            Log.Error("Failed to get list of registered HWIDs: {StatusCode} {ReasonPhrase}",
                registeredHwidsResponse.StatusCode, registeredHwidsResponse.ReasonPhrase);
            return false;
        }

        try
        {
            var registeredHwidsRaw = registeredHwidsResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var registeredHwids = registeredHwidsRaw.Split("\n").Select(x => x.Split(";")[0]);
            var hwid = GeneralUtils.GetHwid(true);
            return registeredHwids.Any(x => x == hwid);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to parse list of registered HWIDs");
        }

        return false;
    }

    /// <summary>
    ///     Requests the lists of dead mirrors from API and excludes them.
    /// </summary>
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

    /// <summary>
    ///     Resets and reloads the mirror list.
    /// </summary>
    /// <param name="keepExcluded">Should not reset excluded mirrors list</param>
    public void ReloadMirrorList(bool keepExcluded = false)
    {
        // Reinitialize the mirror list with the new config and reselect mirror, if needed
        using var op = Operation.Time("Reloading mirror list");
        IsMirrorListInitialized = false;
        _mirrorList = new List<string>();
        if (!keepExcluded)
            ExcludedMirrorList = new List<string>();
        EnsureMirrorListInitialized();
        if (_mirrorList.Contains(MirrorName)) return;
        Log.Information("Mirror {MirrorName} not found in new mirror list", MirrorName);
        MirrorName = "";
        EnsureMirrorSelected();
    }

    /// <summary>
    ///     Excludes current mirror (if set) and switches to random mirror.
    /// </summary>
    /// <exception cref="DownloaderServiceException">Thrown if mirror list is exhausted.</exception>
    private void SwitchMirror()
    {
        if (!string.IsNullOrEmpty(MirrorName))
        {
            MirrorListSemaphoreSlim.Wait();
            try
            {
                Log.Information("Excluding mirror {MirrorName} for this session", MirrorName);
                ExcludedMirrorList.Add(MirrorName);
                _mirrorList.Remove(MirrorName);
                MirrorName = "";
            }
            finally
            {
                MirrorListSemaphoreSlim.Release();
            }
        }

        if (_mirrorList.Count == 0)
            throw new DownloaderServiceException("No mirrors available for this session");
        var random = new Random();
        MirrorName = _mirrorList[random.Next(_mirrorList.Count)];
        Log.Information("Selected mirror: {MirrorName}", MirrorName);
    }

    /// <summary>
    ///     Tries to switch to a specific mirror by user request.
    /// </summary>
    /// <param name="mirrorName">Name of mirror to switch to.</param>
    /// <returns>
    ///     <c>true</c> if switched successfully, <c>false</c> otherwise.
    /// </returns>
    public bool TryManualSwitchMirror(string mirrorName)
    {
        if (!_mirrorList.Contains(mirrorName))
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

    /// <summary>
    ///     Excludes current mirror (if set) from the given mirror list and switches to random mirror from the list.
    /// </summary>
    /// <param name="mirrorList">List of mirrors to use.</param>
    /// <exception cref="DownloaderServiceException">Thrown if given mirror list is exhausted.</exception>
    private void SwitchMirror(IList<string> mirrorList)
    {
        if (!string.IsNullOrEmpty(MirrorName) && mirrorList.Contains(MirrorName))
        {
            Log.Information("Excluding mirror {MirrorName} for this download", MirrorName);
            mirrorList.Remove(MirrorName);
            MirrorName = "";
        }

        if (mirrorList.Count == 0)
            throw new DownloaderServiceException("No mirrors available for this download");
        var random = new Random();
        MirrorName = mirrorList[random.Next(mirrorList.Count)];
        Log.Information("Selected mirror: {MirrorName}", MirrorName);
    }

    /// <summary>
    ///     Ensures that a mirror is selected.
    /// </summary>
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

    /// <summary>
    ///     Ensures that the mirror list is initialized.
    /// </summary>
    /// <exception cref="DownloaderServiceException">Thrown if failed to load mirror list.</exception>
    private void EnsureMirrorListInitialized()
    {
        if (IsMirrorListInitialized) return;
        try
        {
            ExcludeDeadMirrors();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to exclude dead mirrors");
        }

        var mirrorList = GetMirrorList();
        _mirrorList = mirrorList.Where(x => !ExcludedMirrorList.Contains(x)).ToList();
        Log.Debug("Loaded mirrors: {MirrorList}", _mirrorList);
        if (mirrorList.Count == 0)
            throw new DownloaderServiceException("Failed to load mirror list");
        if (_mirrorList.Count == 0)
        {
            Globals.ShowNotification(Resources.Error, Resources.NoMirrorsAvailable, NotificationType.Error,
                TimeSpan.Zero);
            throw new DownloaderServiceException("No mirrors available");
        }
        IsMirrorListInitialized = true;
    }

    /// <summary>
    ///     Runs rclone operation.
    /// </summary>
    /// <param name="source">Source path.</param>
    /// <param name="destination">Destination path.</param>
    /// <param name="operation">Rclone operation type.</param>
    /// <param name="additionalArgs">Additional rclone arguments.</param>
    /// <param name="retries">Number of retries for errors.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>This method waits for rclone config lock and appends current mirror name to source path.</remarks>
    /// <seealso cref="RcloneTransferInternalAsync" />
    private async Task RcloneTransferAsync(string source, string destination, string operation,
        string additionalArgs = "", int retries = 1, CancellationToken ct = default)
    {
        await RcloneConfigSemaphoreSlim.WaitAsync(ct);
        RcloneConfigSemaphoreSlim.Release();
        EnsureMirrorSelected();
        source = $"{MirrorName}:{source}";
        await RcloneTransferInternalAsync(source, destination, operation, additionalArgs, retries, ct);
    }

    /// <summary>
    ///     Runs rclone operation.
    /// </summary>
    /// <param name="source">Source path.</param>
    /// <param name="destination">Destination path.</param>
    /// <param name="operation">Rclone operation type.</param>
    /// <param name="additionalArgs">Additional rclone arguments.</param>
    /// <param name="retries">Number of retries for errors.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="DownloadQuotaExceededException">Thrown if download quota is exceeded on mirror.</exception>
    /// <exception cref="RcloneOperationException">Thrown if a transfer error occured.</exception>
    /// <exception cref="DownloaderServiceException">Thrown if an unknown rclone error occured.</exception>
    private async Task RcloneTransferInternalAsync(string source, string destination, string operation,
        string additionalArgs = "", int retries = 1, CancellationToken ct = default)
    {
        using var op = Operation.Begin("Rclone {Operation} \"{Source}\" -> \"{Destination}\"", 
            operation, source, destination);
        try
        {
            var bwLimit = !string.IsNullOrEmpty(_sideloaderSettings.DownloaderBandwidthLimit)
                ? $"--bwlimit {_sideloaderSettings.DownloaderBandwidthLimit}"
                : "";
            var proxy = GeneralUtils.GetDefaultProxyHostPort();
            var command = Cli.Wrap(PathHelper.RclonePath)
                .WithArguments(
                    $"{operation} --retries {retries} {bwLimit} \"{source}\" \"{destination}\" {additionalArgs}");
            if (proxy is not null)
            {
                Log.Debug("Using proxy {Host}:{Port}", proxy.Value.host, proxy.Value.port);
                command = command.WithEnvironmentVariables(env => env
                    .Set("http_proxy", $"http://{proxy.Value.host}:{proxy.Value.port}")
                    .Set("https_proxy", $"http://{proxy.Value.host}:{proxy.Value.port}"));
            }
            await command.ExecuteBufferedAsync(ct);
            op.Complete();
        }
        catch (Exception e)
        {
            switch (e)
            {
                case OperationCanceledException:
                    throw;
                case CommandExecutionException when e.Message.Contains("downloadQuotaExceeded"):
                    op.SetException(e);
                    throw new DownloadQuotaExceededException(MirrorName, source, e);
                case CommandExecutionException {ExitCode: 1 or 3 or 4 or 7}:
                    if (e.Message.Contains("There is not enough space on the disk") ||
                        e.Message.Contains("no space left on device"))
                    {
                        op.SetException(e);
                        throw new NotEnoughSpaceException(destination, e);
                    }
                    if (!e.Message.Contains("no such host"))
                    {
                        op.SetException(e);
                        throw new RcloneOperationException($"Rclone {operation} error on mirror {MirrorName}", e);
                    }
                    break;
            }

            op.SetException(e);
            throw new DownloaderServiceException($"Error executing rclone {operation}", e);
        }
    }

    /// <summary>
    ///     Ensures that the metadata is available.
    /// </summary>
    /// <param name="refresh">Should force a refresh even if data is already loaded.</param>
    public async Task EnsureMetadataAvailableAsync(bool refresh = false)
    {
        var skip = (AvailableGames is not null && AvailableGames.Count > 0 && !refresh) || Design.IsDesignMode;

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
        }
        catch (Exception e)
        {
            Log.Error(e, "Error downloading game list");
            //Globals.ShowErrorNotification(e, "Error downloading game list");
            throw new DownloaderServiceException("Error downloading game list", e);
        }
        finally
        {
            GameListSemaphoreSlim.Release();
        }

        async Task<bool> TryDownloadGameListAsync()
        {
            // Trying different list files seems useless, just wasting time
            //List<string> gameListNames = new(){"FFA.txt", "FFA2.txt", "FFA3.txt", "FFA4.txt"};
            List<string> gameListNames = new() {"FFA.txt"};
            foreach (var gameListName in gameListNames)
                try
                {
                    await RcloneTransferAsync($"Quest Games/{gameListName}", "./metadata/FFA_new.txt", "copyto");
                    File.Move("./metadata/FFA_new.txt", "./metadata/FFA.txt", true);
                    return true;
                }
                catch (DownloadQuotaExceededException)
                {
                    Log.Debug("Quota exceeded on game list {GameList} on mirror {MirrorName}",
                        gameListName, MirrorName);
                }
                catch (RcloneOperationException e)
                {
                    Log.Warning(e,
                        "Error downloading list {GameList} from mirror {MirrorName} (is mirror down?)",
                        gameListName, MirrorName);
                    return false;
                }

            //Log.Warning("Quota exceeded on all game lists on mirror {MirrorName}", MirrorName);
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
                Globals.ShowNotification(Resources.Error, Resources.FailedToLoadPopularity, NotificationType.Warning,
                    TimeSpan.FromSeconds(5));
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to load popularity data");
                Globals.ShowErrorNotification(e, Resources.FailedToLoadPopularity, NotificationType.Warning,
                    TimeSpan.FromSeconds(5));
            }
        }

        async Task TryLoadDonationBlacklistAsync()
        {
            try
            {
                await RcloneTransferAsync("Quest Games/.meta/nouns/blacklist.txt", "./metadata/blacklist_new.txt",
                    "copyto");
                File.Move(Path.Combine("metadata", "blacklist_new.txt"), Path.Combine("metadata", "blacklist.txt"),
                    true);
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

    /// <summary>
    ///     Downloads the provided game.
    /// </summary>
    /// <param name="game"><see cref="Game" /> to download.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Path to downloaded game.</returns>
    /// <exception cref="DownloaderServiceException">Thrown if an unrecoverable download error occured.</exception>
    public async Task<string> DownloadGameAsync(Game game, CancellationToken ct = default)
    {
        var srcPath = $"Quest Games/{game.ReleaseName}";
        var dstPath = Path.Combine(_sideloaderSettings.DownloadsLocation, game.ReleaseName!);
        var downloadExceptions = new List<Exception>();
        try
        {
            Log.Information("Downloading release {ReleaseName}", game.ReleaseName);
            var localMirrorList = _mirrorList.ToList();
            while (true)
            {
                try
                {
                    await RcloneTransferAsync(srcPath, dstPath,
                        "copy",
                        $"--progress --drive-acknowledge-abuse --rc --rc-addr :{RcloneStatsPort} --drive-stop-on-download-limit",
                        3, ct);
                    if (!Directory.Exists(dstPath))
                    {
                        throw new DirectoryNotFoundException($"Didn't find directory with downloaded files on path \"{dstPath}\"");
                    }
                    var json = JsonConvert.SerializeObject(game, Formatting.Indented);
                    await File.WriteAllTextAsync(Path.Combine(dstPath, "release.json"), json, ct);
                    Task.Run(() => ReportGameDownload(game.PackageName!), ct).SafeFireAndForget();
                    break;
                }
                catch (DownloadQuotaExceededException e)
                {
                    Log.Warning("Quota exceeded on mirror {MirrorName}", MirrorName);
                    downloadExceptions.Add(e);
                }
                catch (RcloneOperationException e)
                {
                    Log.Warning(e, "Download error on mirror {MirrorName}", MirrorName);
                    downloadExceptions.Add(e);
                }

                SwitchMirror(localMirrorList);
                Log.Information("Retrying download");
            }
                

            Log.Information("Release {ReleaseName} downloaded", game.ReleaseName);
            return dstPath;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            downloadExceptions.Insert(0, e);
            throw new DownloaderServiceException("Error downloading release", new AggregateException(downloadExceptions));
        }
    }

    /// <summary>
    ///     Reports the download of the provided package name to the API.
    /// </summary>
    /// <param name="packageName">Package name of the downloaded game.</param>
    private async Task ReportGameDownload(string packageName)
    {
        using var op = Operation.Begin("Reporting game {PackageName} download to API", packageName);
        try
        {
            var dict = new Dictionary<string, string> {{"hwid", GeneralUtils.GetHwid(false)}, {"package_name", packageName}};
            var json = JsonConvert.SerializeObject(dict, Formatting.None);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await ApiHttpClient.PostAsync("reportdownload", content);
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

    /// <summary>
    ///     Starts polling download stats from rclone.
    /// </summary>
    /// <param name="interval">Interval between polls.</param>
    /// <param name="scheduler">Scheduler to run polling on.</param>
    /// <returns>IObservable that provides the stats.</returns>
    public IObservable<(float downloadSpeedBytes, double downloadedBytes)?> PollStats(TimeSpan interval,
        IScheduler scheduler)
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

    /// <summary>
    ///     Gets the download stats from rclone.
    /// </summary>
    /// <returns></returns>
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

    /// <summary>
    ///     Uploads game prepared for donation.
    /// </summary>
    /// <param name="path">Path to packed game.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="FileNotFoundException">Thrown if archive is not found on provided path.</exception>
    /// <exception cref="ArgumentException">Thrown if provided archive has invalid name.</exception>
    public async Task UploadDonationAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Archive not found", path);
        using var op = Operation.At(LogEventLevel.Information, LogEventLevel.Error).Begin("Uploading donation");
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

    /// <summary>
    ///     Downloads trailers addon archive from latest release.
    /// </summary>
    /// <param name="progress"><see cref="IProgress{T}"/> that reports download progress.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Path to downloaded archive.</returns>
    /// <exception cref="DownloaderServiceException">Thrown if trailers addon is already being downloaded.</exception>
    public async Task<string> DownloadTrailersAddon(
        IProgress<(double bytesPerSecond, long downloadedBytes, long totalBytes)>? progress = default, CancellationToken ct = default)
    {
        if (TrailersAddonSemaphoreSlim.CurrentCount == 0)
            throw new DownloaderServiceException("Trailers addon is already downloading");
        await TrailersAddonSemaphoreSlim.WaitAsync(ct);
        using var op = Operation.At(LogEventLevel.Information, LogEventLevel.Error).Begin("Downloading trailers addon");
        var trailersAddonPath = Path.Combine(_sideloaderSettings.DownloadsLocation, "TrailersAddon.zip");
        try
        {
            const string trailersAddonUrl =
                "https://github.com/skrimix/QLoaderFiles/releases/latest/download/TrailersAddon.zip";
            Log.Information("Downloading trailers addon from {TrailersAddonUrl}", trailersAddonUrl);
            if (File.Exists(trailersAddonPath))
                File.Delete(trailersAddonPath);
            var downloadOpt = new DownloadConfiguration
            {
                OnTheFlyDownload = false,
                ChunkCount = 6,
                ParallelDownload = true,
                BufferBlockSize = 8000,
                MaxTryAgainOnFailover = 2,
                RequestConfiguration =
                {
                    UserAgent = ApiHttpClient.DefaultRequestHeaders.UserAgent.ToString(),
                    Proxy = WebRequest.DefaultWebProxy
                }
            };
            var downloader = new DownloadService(downloadOpt);
            downloader.DownloadProgressChanged += (_, _) =>
            {
                if (ct.IsCancellationRequested)
                {
                    downloader.CancelAsync();
                    downloader.Package.Clear();
                }
            };
            downloader.DownloadProgressChanged +=
                GeneralUtils.CreateThrottledEventHandler<Downloader.DownloadProgressChangedEventArgs>((_, args) =>
                {
                    progress?.Report((args.BytesPerSecondSpeed, args.ReceivedBytesSize, args.TotalBytesToReceive));
                }, TimeSpan.FromMilliseconds(100));
            await downloader.DownloadFileTaskAsync(trailersAddonUrl, trailersAddonPath);
            ct.ThrowIfCancellationRequested();

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

    /// <summary>
    ///     Gets the store info about the game.
    /// </summary>
    /// <param name="packageName">Package name to search info for.</param>
    /// <returns><see cref="OculusGame" /> containing the info, or <c>null</c> if no info was found.</returns>
    /// <exception cref="HttpRequestException">Thrown if API request was unsuccessful.</exception>
    public async Task<OculusGame?> GetGameStoreInfo(string? packageName)
    {
        if (packageName is null)
            return null;
        using var op = Operation.Begin("Getting game store info for {PackageName}", packageName);
        var uri = $"oculusgames/{packageName}";
        var response = await ApiHttpClient.GetAsync(uri);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            op.Abandon();
            throw new HttpRequestException($"Failed to get game store info for {packageName}");
        }

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

    public void PruneDownloadedVersions(string releaseName)
    {
        var regex = new Regex(@"(.+) v\d+\+.+");
        var pruningPolicy = _sideloaderSettings.DownloadsPruningPolicy;
        if (pruningPolicy is DownloadsPruningPolicy.KeepAll or DownloadsPruningPolicy.DeleteAfterInstall)
            return;
        var match = regex.Match(releaseName);
        if (!match.Success)
        {
            Log.Debug("Release name {ReleaseName} is non-standard, skipping pruning", releaseName);
            return;
        }
        var gameName = match.Groups[1].Value;
        Log.Debug("Pruning downloaded versions for {GameName}, policy {PruningPolicy}", gameName,
            pruningPolicy);
        var count = 1;
        var keepCount = _sideloaderSettings.DownloadsPruningPolicy switch
        {
            DownloadsPruningPolicy.Keep1Version => 1,
            DownloadsPruningPolicy.Keep2Versions => 2,
            _ => int.MaxValue
        };
        foreach (var directory in Directory.EnumerateDirectories(_sideloaderSettings.DownloadsLocation).OrderByDescending(Path.GetFileName))
        {
            var directoryName = Path.GetFileName(directory);
            match = regex.Match(directoryName);
            if (!match.Success || match.Groups[1].Value != gameName || directoryName == releaseName)
                continue;
            count++;
            if (count <= keepCount) continue;
            Log.Debug("Pruning old version {DirectoryName}", directoryName);
            Directory.Delete(directory, true);
        }
    }

    private static async Task<string> RcloneGetRemoteSizeJson(string remotePath, CancellationToken ct = default)
    {
        await RcloneConfigSemaphoreSlim.WaitAsync(ct);
        RcloneConfigSemaphoreSlim.Release();
        var proxy = GeneralUtils.GetDefaultProxyHostPort();
        var command = Cli.Wrap(PathHelper.RclonePath)
            .WithArguments(
                $"size \"{remotePath}\" --fast-list --json");
        if (proxy is not null)
        {
            Log.Debug("Using proxy {Host}:{Port}", proxy.Value.host, proxy.Value.port);
            command = command.WithEnvironmentVariables(env => env
                .Set("http_proxy", $"http://{proxy.Value.host}:{proxy.Value.port}")
                .Set("https_proxy", $"http://{proxy.Value.host}:{proxy.Value.port}"));
        }
        var result = await command.ExecuteBufferedAsync(ct);
        return result.StandardOutput;
    }

    public async Task<long?> GetGameSizeBytesAsync(Game game, CancellationToken ct = default)
    {
        using var op = Operation.Begin("Rclone calculating size of \"{ReleaseName}\"", game.ReleaseName ?? "N/A");
        try
        {
            await RcloneConfigSemaphoreSlim.WaitAsync(ct);
            RcloneConfigSemaphoreSlim.Release();
            EnsureMirrorSelected();
            var remotePath = $"{MirrorName}:Quest Games/{game.ReleaseName}";
            var sizeJson = await RcloneGetRemoteSizeJson(remotePath, ct);
            var dict = JsonConvert.DeserializeObject<Dictionary<string, long>>(sizeJson);
            if (dict is null)
                return null;
            if (!dict.TryGetValue("bytes", out var sizeBytes)) return null;
            op.Complete();
            return sizeBytes;
        }
        catch (Exception e)
        {
            switch (e)
            {
                case OperationCanceledException:
                    throw;
                case CommandExecutionException {ExitCode: 1 or 3 or 4 or 7}:
                    if (!e.Message.Contains("no such host"))
                    {
                        op.SetException(e);
                        throw new RcloneOperationException($"Rclone size error on mirror {MirrorName}", e);
                    }
                    break;
            }

            op.SetException(e);
            throw new DownloaderServiceException("Error executing rclone size", e);
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
    public DownloadQuotaExceededException(string mirrorName, string remotePath, Exception inner)
        : base($"Quota exceeded on mirror {mirrorName}", inner)
    {
        MirrorName = mirrorName;
        RemotePath = remotePath;
    }

    public string MirrorName { get; }
    public string RemotePath { get; }
}

public class RcloneOperationException : DownloaderServiceException
{
    public RcloneOperationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public class NotEnoughSpaceException : DownloaderServiceException
{
    public NotEnoughSpaceException(string path, Exception inner)
        : base($"Not enough disk space on {path}", inner)
    {
        Path = path;
    }

    public string Path { get; }
}