using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QSideloader.Exceptions;
using QSideloader.Models;
using Serilog;
using SerilogTimings;

namespace QSideloader.Utilities;

public static class ApiClient
{
    private const string StaticFilesUrl = "https://qloader.5698452.xyz/files/";
    private const string ApiUrl = "https://qloader.5698452.xyz/api/v1/";

    static ApiClient()
    {
        var apiHttpClientHandler = new HttpClientHandler
        {
            Proxy = WebRequest.DefaultWebProxy
        };
        ApiHttpClient = new HttpClient(apiHttpClientHandler) {BaseAddress = new Uri(ApiUrl)};
        var appName = Program.Name;
        var appVersion = Assembly.GetExecutingAssembly().GetName().Version;
        var appVersionString = appVersion is not null
            ? $"{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}"
            : "Unknown";
        ApiHttpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(appName, appVersionString));
        // set timeout to 10 seconds
        ApiHttpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    private static HttpClient ApiHttpClient { get; }
    public static string HttpUserAgent => ApiHttpClient.DefaultRequestHeaders.UserAgent.ToString();

    /// <summary>
    /// Retrieves the rclone config.
    /// </summary>
    /// <returns>Contents and name of the retrieved file.</returns>
    /// <exception cref="ApiException"></exception>
    public static async Task<(string fileName, string content)> GetRcloneConfig(string? overrideUrl = null)
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

    public static async Task<List<Dictionary<string, JsonElement>>?> GetDeadMirrors()
    {
        return await ApiHttpClient.GetFromJsonAsync<List<Dictionary<string, JsonElement>>>("mirrors?status=DOWN");
    }

    /// <summary>
    /// Retrieves the popularity stats.
    /// </summary>
    /// <returns></returns>
    public static async Task<List<Dictionary<string, JsonElement>>?> GetPopularity()
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
    public static async Task<string> GetDonationBlacklist()
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
    public static async Task ReportGameDownload(string packageName)
    {
        using var op = Operation.Begin("Reporting game {PackageName} download to API", packageName);
        try
        {
            var dict = new Dictionary<string, string>
                {{"hwid", GeneralUtils.GetHwid(true)}, {"package_name", packageName}};
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

    /// <summary>
    ///     Gets the store info about the game.
    /// </summary>
    /// <param name="packageName">Package name to search info for.</param>
    /// <returns><see cref="OculusGame" /> containing the info, or <c>null</c> if no info was found.</returns>
    /// <exception cref="HttpRequestException">Thrown if API request was unsuccessful.</exception>
    public static async Task<OculusGame?> GetGameStoreInfo(string? packageName)
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
}