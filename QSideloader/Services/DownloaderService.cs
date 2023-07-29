using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
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
using QSideloader.Exceptions;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Utilities;
using QSideloader.ViewModels;
using Serilog;
using Serilog.Events;
using SerilogTimings;

namespace QSideloader.Services;

/// <summary>
///     Service for all download/upload operations.
/// </summary>
public partial class DownloaderService
{
    private static readonly SemaphoreSlim MirrorListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim GameListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim DownloadSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim RcloneConfigSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim TrailersAddonSemaphoreSlim = new(1, 1);
    private readonly SettingsData _sideloaderSettings;
    private List<string> _mirrorList = new();
    private bool? _donationsAvailable;

    static DownloaderService()
    {
        var httpClientHandler = new HttpClientHandler
        {
            Proxy = WebRequest.DefaultWebProxy
        };
        HttpClient = new HttpClient(httpClientHandler);
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
    public IEnumerable<string> MirrorList => _mirrorList.AsReadOnly();
    public static int RcloneStatsPort => 48040;

    private static bool CanSwitchMirror => RcloneConfigSemaphoreSlim.CurrentCount > 0 &&
                                           MirrorListSemaphoreSlim.CurrentCount > 0 &&
                                           GameListSemaphoreSlim.CurrentCount > 0;

    private bool IsMirrorListInitialized { get; set; }
    private static HttpClient HttpClient { get; }


    /// <summary>
    ///     Downloads a new config file for rclone from a mirror.
    /// </summary>
    private async Task UpdateRcloneConfigAsync()
    {
        await RcloneConfigSemaphoreSlim.WaitAsync();
        try
        {
            var overrideUrl = Globals.Overrides.TryGetValue("ConfigUpdateUrl", out var @override) ? @override : null;
            var usingOverride = !string.IsNullOrEmpty(overrideUrl);
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(PathHelper.RclonePath)!, "FFA_config")) &&
                string.IsNullOrWhiteSpace(overrideUrl))
            {
                Log.Warning("FFA_config not found, skipping rclone config update");
                return;
            }

            Log.Information("Updating rclone config");
            if (usingOverride)
            {
                Log.Information("Using override config url: {OverrideUrl}", overrideUrl);
            }
            if (await TryDownloadConfigFromServer(overrideUrl))
            {
                Log.Information("Rclone config updated from server");
                return;
            }

            if (usingOverride)
            {
                Log.Warning("Failed to update rclone config from override url");
                return;
            }

            await EnsureMirrorSelectedAsync();
            var localMirrorList = _mirrorList.ToList();
            while (true)
            {
                if (await TryDownloadConfigAsync())
                {
                    Log.Information("Rclone config updated from {MirrorName}", MirrorName);
                    await ReloadMirrorListAsync(true);
                    return;
                }

                SwitchMirror(localMirrorList);
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

        async Task<bool> TryDownloadConfigFromServer(string? overrideConfigUrl = null)
        {
            try
            {
                var (fileName, config) = await ApiClient.GetRcloneConfigAsync(overrideConfigUrl);
                var oldConfigPath = Path.Combine(Path.GetDirectoryName(PathHelper.RclonePath)!, fileName);
                var newConfigPath = Path.Combine(Path.GetDirectoryName(PathHelper.RclonePath)!, fileName + "_new");
                await File.WriteAllTextAsync(newConfigPath, config);
                File.Move(newConfigPath, oldConfigPath, true);
                return true;
            }
            catch (Exception e)
            {
                Log.Warning(e, "Couldn't download rclone config from server");
                return false;
            }
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
        await EnsureMirrorSelectedAsync();
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
    private static async Task<List<string>> GetMirrorListAsync()
    {
        List<string> mirrorList = new();

        var result = await ExecuteRcloneCommandAsync("listremotes");
        var matches = RcloneMirrorRegex().Matches(result.StandardOutput);
        foreach (Match match in matches) mirrorList.Add(match.Groups[1].ToString());
        return mirrorList;
    }

    public async Task<bool> GetDonationsAvailable()
    {
        if (_donationsAvailable is not null) return _donationsAvailable.Value;
        var result = await ExecuteRcloneCommandAsync("listremotes");
        _donationsAvailable = result.StandardOutput.Contains("FFA-DD:");
        return _donationsAvailable.Value;
    }

    /// <summary>
    ///     Requests the lists of dead mirrors from API and excludes them.
    /// </summary>
    private void ExcludeDeadMirrors()
    {
        var list = Task.Run(async () => await ApiClient.GetDeadMirrorsAsync()).Result;
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
    public async Task ReloadMirrorListAsync(bool keepExcluded = false)
    {
        // Reinitialize the mirror list with the new config and reselect mirror, if needed
        using var op = Operation.Time("Reloading mirror list");
        IsMirrorListInitialized = false;
        _mirrorList = new List<string>();
        if (!keepExcluded)
            ExcludedMirrorList = new List<string>();
        await EnsureMirrorListInitializedAsync();
        if (_mirrorList.Contains(MirrorName)) return;
        Log.Information("Mirror {MirrorName} not found in new mirror list", MirrorName);
        MirrorName = "";
        await EnsureMirrorSelectedAsync();
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
            throw new NoMirrorsAvailableException(true, ExcludedMirrorList.Count);
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
    public async Task<bool> TryManualSwitchMirrorAsync(string mirrorName)
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
        try
        {
            await EnsureMetadataAvailableAsync(true);
            return true;
        }
        catch
        {
            return false;
        }
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
            throw new NoMirrorsAvailableException(false, ExcludedMirrorList.Count);
        var random = new Random();
        MirrorName = mirrorList[random.Next(mirrorList.Count)];
        Log.Information("Selected mirror: {MirrorName}", MirrorName);
    }

    /// <summary>
    ///     Ensures that a mirror is selected.
    /// </summary>
    private async Task EnsureMirrorSelectedAsync()
    {
        await MirrorListSemaphoreSlim.WaitAsync();
        try
        {
            await EnsureMirrorListInitializedAsync();

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
    private async Task EnsureMirrorListInitializedAsync()
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

        var mirrorList = await GetMirrorListAsync();
        _mirrorList = mirrorList.Where(x => !ExcludedMirrorList.Contains(x)).ToList();
        Log.Debug("Loaded mirrors: {MirrorList}", _mirrorList);
        if (mirrorList.Count == 0)
            throw new DownloaderServiceException("Mirror list is empty");
        if (_mirrorList.Count == 0)
        {
            Globals.ShowNotification(Resources.Error, Resources.NoMirrorsAvailable, NotificationType.Error,
                TimeSpan.Zero);
            throw new NoMirrorsAvailableException(true, ExcludedMirrorList.Count);
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
        await EnsureMirrorSelectedAsync();
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

            await ExecuteRcloneCommandAsync(
                $"{operation} --retries {retries} {bwLimit} \"{source}\" \"{destination}\" {additionalArgs}",
                ct);
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
    /// Calls rclone executable with given arguments.
    /// </summary>
    /// <param name="arguments">Arguments to pass to rclone.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="BufferedCommandResult"/> of rclone command.</returns>
    /// <exception cref="HwidCheckException">Thrown if rclone returned hwid verification error.</exception>
    private static async Task<BufferedCommandResult> ExecuteRcloneCommandAsync(string arguments,
        CancellationToken ct = default)
    {
        var proxy = GeneralUtils.GetDefaultProxyHostPort();
        var command = Cli.Wrap(PathHelper.RclonePath)
            .WithArguments(arguments);
        if (proxy is not null)
        {
            Log.Debug("Using proxy {Host}:{Port}", proxy.Value.host, proxy.Value.port);
            command = command.WithEnvironmentVariables(env => env
                .Set("http_proxy", $"http://{proxy.Value.host}:{proxy.Value.port}")
                .Set("https_proxy", $"http://{proxy.Value.host}:{proxy.Value.port}"));
        }

        try
        {
            using var forcefulCts = new CancellationTokenSource();
            // When the cancellation token is triggered,
            // schedule forceful cancellation as a fallback.
            await using var link = ct.Register(() =>
                // ReSharper disable once AccessToDisposedClosure
                forcefulCts.CancelAfter(TimeSpan.FromSeconds(3))
            );
            var result = await command.ExecuteBufferedAsync(Console.OutputEncoding, Console.OutputEncoding,
                forcefulCts.Token, ct);
            return result;
        }
        catch (CommandExecutionException e)
        {
            if (!e.Message.Contains("Could not verify HWID")) throw;
            var ex = new HwidCheckException(e);
            Globals.ShowErrorNotification(ex, Resources.CouldntVerifyVip);
            throw ex;
        }
    }

    /// <summary>
    ///     Ensures that the metadata is available.
    /// </summary>
    /// <param name="refresh">Should force a refresh even if data is already loaded.</param>
    public async Task EnsureMetadataAvailableAsync(bool refresh = false)
    {
        var skip = AvailableGames is not null && AvailableGames.Count > 0 && !refresh || Design.IsDesignMode;

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
            await EnsureMirrorSelectedAsync();

            var csvEngine = new FileHelperEngine<Game>();
            if (AvailableGames is not null && !refresh)
                return;
            AvailableGames = null;
            var emptyRetries = 0;
            Log.Information("Downloading game list");
            while (true)
            {
                if (await TryDownloadGameListAsync())
                    try
                    {
                        AvailableGames = csvEngine.ReadFile(Path.Combine("metadata", "FFA.txt")).ToList();
                        if (AvailableGames.Count == 0 && emptyRetries < 3)
                        {
                            Log.Warning("Loaded empty game list from mirror {MirrorName}, retrying", MirrorName);
                            emptyRetries++;
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
                var popularity = await ApiClient.GetPopularityAsync();

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

        async Task<bool> TryLoadDonationBlacklistFromServerAsync()
        {
            try
            {
                var blacklist = await ApiClient.GetDonationBlacklistAsync();
                await File.WriteAllTextAsync(Path.Combine("metadata", "blacklist_new.txt"), blacklist);
                File.Move(Path.Combine("metadata", "blacklist_new.txt"), Path.Combine("metadata", "blacklist.txt"),
                    true);
                Log.Debug("Downloaded donation blacklist from server");
                return true;
            }
            catch (Exception e)
            {
                Log.Warning(e, "Couldn't download donation blacklist from server");
                return false;
            }
        }

        async Task TryLoadDonationBlacklistAsync()
        {
            if (!await TryLoadDonationBlacklistFromServerAsync())
                try
                {
                    await RcloneTransferAsync("Quest Games/.meta/nouns/blacklist.txt", "./metadata/blacklist_new.txt",
                        "copyto");
                    File.Move(Path.Combine("metadata", "blacklist_new.txt"), Path.Combine("metadata", "blacklist.txt"),
                        true);
                    Log.Debug("Downloaded donation blacklist from {MirrorName}", MirrorName);
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
                        throw new DirectoryNotFoundException(
                            $"Didn't find directory with downloaded files on path \"{dstPath}\"");
                    var json = JsonSerializer.Serialize(game, new JsonSerializerOptions
                    {
                        TypeInfoResolver = JsonSerializerContext.Default,
                        WriteIndented = true
                    });
                    await File.WriteAllTextAsync(Path.Combine(dstPath, "release.json"), json, ct);
                    Task.Run(() => ApiClient.ReportGameDownloadAsync(game.PackageName!), ct).SafeFireAndForget();
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
            // List of error messages to show to the user
            var errorMessages = downloadExceptions.Select(x => x.Message).ToList();
            var message =
                $"Failed to download release\nThe following errors occured (in reverse order):\n{string.Join("\n", errorMessages)}\n";
            throw new DownloaderServiceException(message, new AggregateException(downloadExceptions));
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
    public static IObservable<(double downloadSpeedBytes, double downloadedBytes)?> PollStats(TimeSpan interval,
        IScheduler scheduler)
    {
        return Observable.Create<(double downloadSpeedBytes, double downloadedBytes)?>(observer =>
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
    private static async Task<(double downloadSpeedBytes, double downloadedBytes)?> GetRcloneDownloadStats()
    {
        try
        {
            var response = await HttpClient.PostAsync($"http://127.0.0.1:{RcloneStatsPort}/core/stats", null);
            var responseContent = await response.Content.ReadAsStringAsync();
            var results =
                JsonSerializer.Deserialize(responseContent, JsonSerializerContext.Default.DictionaryStringObject);
            if (results is null || !results.ContainsKey("transferring")) return null;
            if (!((JsonElement)results["speed"]).TryGetDouble(out var downloadSpeedBytes)) return null;
            if (!((JsonElement)results["bytes"]).TryGetDouble(out var downloadedBytes)) return null;
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
        if (!DonationArchiveRegex().IsMatch(archiveName))
            throw new ArgumentException("Invalid archive name", nameof(path));
        Log.Information("Uploading donation {ArchiveName}", archiveName);
        var md5Sum = GeneralUtils.GetMd5FileChecksum(path).ToLower();
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
        IProgress<(double bytesPerSecond, long downloadedBytes, long totalBytes)>? progress = default,
        CancellationToken ct = default)
    {
        if (TrailersAddonSemaphoreSlim.CurrentCount == 0)
            throw new DownloaderServiceException("Trailers addon is already downloading");
        await TrailersAddonSemaphoreSlim.WaitAsync(ct);
        using var op = Operation.At(LogEventLevel.Information, LogEventLevel.Error).Begin("Downloading trailers addon");
        var trailersAddonPath = Path.Combine(_sideloaderSettings.DownloadsLocation, "TrailersAddon.zip");
        try
        {
            const string trailersAddonUrl =
                "https://qloader.5698452.xyz/files/TrailersAddon.zip";
            Log.Information("Downloading trailers addon from {TrailersAddonUrl}", trailersAddonUrl);
            if (File.Exists(trailersAddonPath))
                File.Delete(trailersAddonPath);
            var downloadOpt = new DownloadConfiguration
            {
                ChunkCount = 6,
                ParallelDownload = true,
                BufferBlockSize = 8000,
                MaxTryAgainOnFailover = 2,
                RequestConfiguration =
                {
                    UserAgent = ApiClient.HttpUserAgent,
                    Proxy = WebRequest.DefaultWebProxy
                }
            };
            var downloader = new DownloadService(downloadOpt);
            downloader.DownloadProgressChanged +=
                GeneralUtils.CreateThrottledEventHandler<Downloader.DownloadProgressChangedEventArgs>(
                    (_, args) =>
                    {
                        progress?.Report((args.BytesPerSecondSpeed, args.ReceivedBytesSize, args.TotalBytesToReceive));
                    }, TimeSpan.FromMilliseconds(100));
            await downloader.DownloadFileTaskAsync(trailersAddonUrl, trailersAddonPath + ".tmp", ct);
            File.Move(trailersAddonPath + ".tmp", trailersAddonPath, true);
            ct.ThrowIfCancellationRequested();

            op.Complete();
            return trailersAddonPath;
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException)
            {
                if (File.Exists(trailersAddonPath + ".tmp"))
                    File.Delete(trailersAddonPath + ".tmp");
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
    /// Prunes downloaded versions of the release according to the pruning policy.
    /// </summary>
    /// <param name="releaseName">Name of the release to prune.</param>
    /// <remarks>
    /// Pruning will be skipped unless the release name is in the following format:
    /// <code>Game Name v1.2.3+456</code>
    /// </remarks>
    /// <seealso cref="SideloaderSettingsViewModel.DownloadsPruningPolicy"/>
    public void PruneDownloadedVersions(string releaseName)
    {
        var pruningPolicy = _sideloaderSettings.DownloadsPruningPolicy;
        if (pruningPolicy is DownloadsPruningPolicy.KeepAll or DownloadsPruningPolicy.DeleteAfterInstall)
            return;
        var match = StandardReleaseNameRegex().Match(releaseName);
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
        foreach (var directory in Directory.EnumerateDirectories(_sideloaderSettings.DownloadsLocation)
                     .OrderByDescending(Path.GetFileName))
        {
            var directoryName = Path.GetFileName(directory);
            match = StandardReleaseNameRegex().Match(directoryName);
            if (!match.Success || match.Groups[1].Value != gameName || directoryName == releaseName)
                continue;
            count++;
            if (count <= keepCount) continue;
            Log.Debug("Pruning old version {DirectoryName}", directoryName);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Calculates size of the remote directory.
    /// </summary>
    /// <param name="remotePath">Path to the remote directory.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Output of <c>rclone size</c> command.</returns>
    private static async Task<string> RcloneGetRemoteSizeJson(string remotePath, CancellationToken ct = default)
    {
        await RcloneConfigSemaphoreSlim.WaitAsync(ct);
        RcloneConfigSemaphoreSlim.Release();

        var result = await ExecuteRcloneCommandAsync($"size \"{remotePath}\" --fast-list --json", ct);
        return result.StandardOutput;
    }

    /// <summary>
    /// Calculates size of the game game release
    /// </summary>
    /// <param name="game">Game to calculate size of.</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Size of the game release in bytes or <c>null</c> if the size could not be calculated.</returns>
    /// <exception cref="RcloneOperationException">Thrown if rclone command returned error.</exception>
    /// <exception cref="DownloaderServiceException">Thrown if an unknown error occured.</exception>
    public async Task<long?> GetGameSizeBytesAsync(Game game, CancellationToken ct = default)
    {
        using var op = Operation.Begin("Rclone calculating size of \"{ReleaseName}\"", game.ReleaseName ?? "N/A");
        try
        {
            await RcloneConfigSemaphoreSlim.WaitAsync(ct);
            RcloneConfigSemaphoreSlim.Release();
            await EnsureMirrorSelectedAsync();
            var remotePath = $"{MirrorName}:Quest Games/{game.ReleaseName}";
            var sizeJson = await RcloneGetRemoteSizeJson(remotePath, ct);
            var dict = JsonSerializer.Deserialize(sizeJson, JsonSerializerContext.Default.DictionaryStringInt64);
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

    /// <summary>
    /// Regex pattern to parse mirror names from rclone output.
    /// </summary>
    [GeneratedRegex("(FFA-\\d+):")]
    private static partial Regex RcloneMirrorRegex();
    
    [GeneratedRegex("^.+ v\\d+ .+\\.zip$")]
    private static partial Regex DonationArchiveRegex();
    
    [GeneratedRegex("(.+) v\\d+\\+.+")]
    private static partial Regex StandardReleaseNameRegex();
}