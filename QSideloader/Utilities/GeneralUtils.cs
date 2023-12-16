using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Net.Http;
using CliWrap;
using CliWrap.Buffered;
using QSideloader.Models;
using Serilog;
using SerilogTimings;

namespace QSideloader.Utilities;

public static partial class GeneralUtils
{
    private static HttpClient HttpClient { get; }

    static GeneralUtils()
    {
        var handler = new HttpClientHandler
        {
            Proxy = WebRequest.DefaultWebProxy
        };
        HttpClient = new HttpClient(handler);
    }

    public static string GetMd5FileChecksum(string filename)
    {
        using var hasher = MD5.Create();
        using var stream = File.OpenRead(filename);
        var hash = hasher.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "");
    }

    public static async Task<ApkInfo> GetApkInfoAsync(string apkPath)
    {
        if (!File.Exists(apkPath))
            throw new FileNotFoundException("Apk file not found", apkPath);

        var aaptOutput = await Cli.Wrap(PathHelper.AaptPath)
            .WithArguments($"dump badging \"{apkPath}\"")
            .ExecuteBufferedAsync();

        var apkInfo = new ApkInfo
        {
            ApplicationLabel = ApplicationLabelRegex().Match(aaptOutput.StandardOutput).Groups[1].Value,
            PackageName = PmPackageNameRegex().Match(aaptOutput.StandardOutput).Groups[1].Value,
            VersionCode = int.Parse(VersionCodeRegex().Match(aaptOutput.StandardOutput).Groups[1].Value)
            //VersionName = VersionNameRegex().Match(aaptOutput.StandardOutput).Groups[1].Value
        };
        return apkInfo;
    }


    public static async Task InstallTrailersAddonAsync(string path, bool delete)
    {
        using var op = Operation.Begin("Installing trailers addon");
        if (!File.Exists(path))
            throw new FileNotFoundException("Trailers addon archive not found", path);
        await ZipUtil.ExtractArchiveAsync(path, Directory.GetCurrentDirectory());
        if (delete)
            File.Delete(path);
        op.Complete();
    }

    public static bool IsDirectoryWritable(string dirPath)
    {
        try
        {
            using var fs = File.Create(Path.Combine(dirPath, Path.GetRandomFileName()), 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static EventHandler<T> CreateThrottledEventHandler<T>(
        EventHandler<T> handler,
        TimeSpan throttle)
    {
        var throttling = false;
        return (s, e) =>
        {
            if (throttling) return;
            handler(s, e);
            throttling = true;
            Task.Delay(throttle).ContinueWith(_ => throttling = false);
        };
    }

    public static (string host, int port)? GetDefaultProxyHostPort()
    {
        const string testUrl = "http://google.com";
        try
        {
            var proxyUri = WebRequest.DefaultWebProxy?.GetProxy(new Uri(testUrl));
            if (proxyUri is null)
                return null;
            return (proxyUri.Host, proxyUri.Port);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to get system proxy host and port");
            return null;
        }
    }

    public static string GetOsName()
    {
        return OperatingSystem.IsWindows() ? "Windows" :
            OperatingSystem.IsLinux() ? "Linux" :
            OperatingSystem.IsMacOS() ? "OSX" :
            "Unknown";
    }

    /// <summary>
    /// Creates a paste on sprunge.us.
    /// </summary>
    /// <param name="text">Text content for paste.</param>
    /// <returns>Link to created paste.</returns>
    public static async Task<string> CreatePasteAsync(string text)
    {
        const string url = "http://sprunge.us";
        var formContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("sprunge", text)
        });
        var response = await HttpClient.PostAsync(url, formContent);
        return await response.Content.ReadAsStringAsync();
    }

    public static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(fileName.Where(c => !invalidChars.Contains(c)).ToArray());
    }

    public static Dictionary<string, string?> ParseOverridesConfig()
    {
        var overrides = new Dictionary<string, string?>
        {
            {"ConfigUpdateUrl", null},
            {"DisableSelfUpdate", "0"},
            {"ApiUrl", null}
        };
        if (!File.Exists(PathHelper.OverridesPath))
            return overrides;
        var lines = File.ReadAllLines(PathHelper.OverridesPath);
        foreach (var line in lines)
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
                continue;
            var split = line.Split('=');
            if (split.Length != 2)
            {
                Log.Warning("Invalid line in overrides file: {Line}", line);
                continue;
            }

            var key = split[0].Trim();
            var value = string.IsNullOrWhiteSpace(split[1]) ? null : split[1].Trim();
            if (overrides.ContainsKey(key))
                overrides[key] = value;
            else
            {
                Log.Warning("Unknown key in overrides file: {Key}", key);
            }
        }

        Log.Information("Loaded overrides: {Overrides}", overrides);

        return overrides;
    }

    public static string GetIanaTimeZoneId(TimeZoneInfo tzi)
    {
        if (tzi.HasIanaId)
            return tzi.Id; // no conversion necessary

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(tzi.Id, out var ianaId))
            return ianaId; // use the converted ID

        throw new TimeZoneNotFoundException($"No IANA time zone found for \"{tzi.Id}\".");
    }

    [GeneratedRegex("application-label:'(.*)'")]
    private static partial Regex ApplicationLabelRegex();

    [GeneratedRegex("package: name='(.*?)'")]
    private static partial Regex PmPackageNameRegex();

    [GeneratedRegex("versionCode='(.*?)'")]
    private static partial Regex VersionCodeRegex();

    [GeneratedRegex("versionName='(.*?)'")]
    private static partial Regex VersionNameRegex();
}