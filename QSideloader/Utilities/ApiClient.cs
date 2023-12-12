using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using QSideloader.Common;
using QSideloader.Exceptions;
using QSideloader.Models;
using Serilog;
using SerilogTimings;

namespace QSideloader.Utilities;

public static class ApiClient
{
    private const string StaticFilesUrl = "https://qloader.5698452.xyz/files/";
    private const string DefaultApiUrl = "https://qloader.5698452.xyz/api/v1/";

    static ApiClient()
    {
        var apiHttpClientHandler = new HttpClientHandler
        {
            Proxy = WebRequest.DefaultWebProxy
        };
        Globals.Overrides.TryGetValue("ApiUrl", out var apiUrl);
        if (apiUrl is not null)
        {
            Log.Information("Using API URL override: {ApiUrl}", apiUrl);
        }
        else
        {
            apiUrl = DefaultApiUrl;
        }
        ApiHttpClient = new HttpClient(apiHttpClientHandler) {BaseAddress = new Uri(apiUrl)};
        var appName = Program.Name;
        var appVersion = Assembly.GetExecutingAssembly().GetName().Version;
        var appVersionString = appVersion is not null
            ? $"{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}"
            : "Unknown";
        ApiHttpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(appName, appVersionString));
        // set timeout for requests
        ApiHttpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    private static HttpClient ApiHttpClient { get; }
    public static string HttpUserAgent => ApiHttpClient.DefaultRequestHeaders.UserAgent.ToString();

    /// <summary>
    /// Retrieves the rclone config.
    /// </summary>
    /// <returns>Contents and name of the retrieved file.</returns>
    /// <exception cref="ApiException"></exception>
    public static async Task<(string fileName, string content)> GetRcloneConfigAsync(string? overrideUrl = null)
    {
        try
        {
            var configUrl = overrideUrl ?? $"{StaticFilesUrl}FFA_config";
            var response = await ApiHttpClient.GetAsync(configUrl);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return (
                response.Content.Headers.ContentDisposition?.FileName ??
                Uri.UnescapeDataString(new Uri(configUrl).Segments.Last()), content);
        }
        catch (Exception e)
        {
            throw new ApiException("Failed to get rclone config", e);
        }
    }

    /// <summary>
    ///     Downloads the rclone binary to the provided path, if it doesn't exist or is outdated.
    /// </summary>
    /// <param name="currentModTime">Current modification time of the binary.</param>
    /// <param name="currentSize">Current size of the binary, in bytes.</param>
    /// <param name="outFileName">Path to download the binary to.</param>
    /// <exception cref="ApiException"></exception>
    public static async Task DownloadRcloneBinaryAsync(DateTime? currentModTime, long currentSize, string outFileName)
    {
        try
        {
            // ReSharper disable once SwitchExpressionHandlesSomeKnownEnumValuesWithExceptionInDefault
            var binaryUrl = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 when OperatingSystem.IsWindows() => $"{StaticFilesUrl}rclone/windows/FFA.exe",
                Architecture.X64 when OperatingSystem.IsLinux() => $"{StaticFilesUrl}rclone/linux/FFA-x64",
                Architecture.Arm64 when OperatingSystem.IsLinux() => $"{StaticFilesUrl}rclone/linux/FFA-arm64",
                Architecture.X64 when OperatingSystem.IsMacOS() => $"{StaticFilesUrl}rclone/darwin/FFA-x64",
                Architecture.Arm64 when OperatingSystem.IsMacOS() => $"{StaticFilesUrl}rclone/darwin/FFA-arm64",
                _ => throw new PlatformNotSupportedException("No rclone binary download available for this platform")
            };

            // We need only the headers
            using var response = await ApiHttpClient.GetAsync(binaryUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            
            // Check modification time and size, and skip download if they match
            Log.Debug("Remote rclone modtime: {ModTime}, size: {Size}", response.Content.Headers.LastModified,
                response.Content.Headers.ContentLength);
            Log.Debug("Local rclone modtime: {ModTime}, size: {Size}", currentModTime, currentSize);
            if (currentModTime is not null && response.Content.Headers.LastModified <= currentModTime &&
                response.Content.Headers.ContentLength == currentSize)
            {
                Log.Information("Rclone binary is up to date");
                return;
            }

            await using var fs = File.Create(outFileName);
            await response.Content.CopyToAsync(fs);
            // Set modification time
            File.SetLastWriteTime(outFileName, response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow);
        }
        catch (Exception e)
        {
            throw new ApiException("Failed to download rclone binary", e);
        }
    }

    public static Task<List<Dictionary<string, JsonElement>>?> GetDeadMirrorsAsync()
    {
        return ApiHttpClient.GetFromJsonAsync<List<Dictionary<string, JsonElement>>>("mirrors?status=DOWN");
    }

    /// <summary>
    /// Retrieves the popularity stats.
    /// </summary>
    /// <returns></returns>
    public static async Task<List<Dictionary<string, JsonElement>>?> GetPopularityAsync()
    {
        // ReSharper disable once ConvertToUsingDeclaration
        using (var _ = Operation.Time("Requesting popularity from API"))
        {
            return
                await ApiHttpClient.GetFromJsonAsync<List<Dictionary<string, JsonElement>>>("popularity");
        }
    }

    /// <summary>
    /// Retrieves the donation blacklist.
    /// </summary>
    /// <returns></returns>
    public static async Task<string> GetDonationBlacklistAsync()
    {
        const string configUrl = $"{StaticFilesUrl}blacklist.txt";
        var response = await ApiHttpClient.GetAsync(configUrl);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    ///     Reports the download of the provided package name.
    /// </summary>
    /// <param name="packageName">Package name of the downloaded game.</param>
    public static async Task ReportGameDownloadAsync(string packageName)
    {
        using var op = Operation.Begin("Reporting game {PackageName} download to API", packageName);
        try
        {
            var dict = new Dictionary<string, string>
                {{"hwid", Hwid.GetHwid(true)}, {"package_name", packageName}};
            var json = JsonSerializer.Serialize(dict, JsonSerializerContext.Default.DictionaryStringString);
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

    /// <summary>
    ///     Gets the store info about the game.
    /// </summary>
    /// <param name="packageName">Package name to search info for.</param>
    /// <returns><see cref="OculusGame" /> containing the info, or <c>null</c> if no info was found.</returns>
    /// <exception cref="HttpRequestException">Thrown if API request was unsuccessful.</exception>
    public static async Task<OculusGame?> GetGameStoreInfoAsync(string? packageName)
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

        var game = JsonSerializer.Deserialize(responseContent, JsonSerializerContext.Default.OculusGame);
        op.Complete();
        return game;
    }
}