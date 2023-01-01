using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Management;
using System.Net.Http;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Win32;
using QSideloader.Models;
using Serilog;
using SerilogTimings;

namespace QSideloader.Utilities;

public static class GeneralUtils
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

    public static ApkInfo GetApkInfo(string apkPath)
    {
        if (!File.Exists(apkPath))
            throw new FileNotFoundException("Apk file not found", apkPath);

        var aaptOutput = Cli.Wrap(PathHelper.AaptPath)
            .WithArguments($"dump badging \"{apkPath}\"")
            .ExecuteBufferedAsync().GetAwaiter().GetResult();

        var apkInfo = new ApkInfo
        {
            ApplicationLabel = Regex.Match(aaptOutput.StandardOutput, "application-label:'(.*?)'").Groups[1].Value,
            PackageName = Regex.Match(aaptOutput.StandardOutput, "package: name='(.*?)'").Groups[1].Value,
            VersionCode = int.Parse(Regex.Match(aaptOutput.StandardOutput, "versionCode='(.*?)'").Groups[1].Value),
            VersionName = Regex.Match(aaptOutput.StandardOutput, "versionName='(.*?)'").Groups[1].Value
        };
        return apkInfo;
    }

    /// <summary>
    ///     Gets current system HWID.
    /// </summary>
    /// <param name="useCompatOnWindows">Use <see cref="GetHwidCompat"/> if on Windows.</param>
    /// <returns>HWID as <see cref="string" />.</returns>
    public static string GetHwid(bool useCompatOnWindows)
    {
        string? hwid = null;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (File.Exists("/var/lib/dbus/machine-id"))
                    hwid = File.ReadAllText("/var/lib/dbus/machine-id");
                if (File.Exists("/etc/machine-id"))
                    hwid = File.ReadAllText("/etc/machine-id");
            }

            // This algorithm is different from windows Loader v2 as that one fails on some systems
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (useCompatOnWindows)
                    return GetHwidCompat();
                var regKey =
                    Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography", false);
                var regValue = regKey?.GetValue("MachineGuid") ??
                               throw new InvalidOperationException("Failed to get HWID");
                hwid = regValue.ToString();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var ioregOutput = Cli.Wrap("ioreg")
                    .WithArguments("-rd1 -c IOPlatformExpertDevice")
                    .ExecuteBufferedAsync().GetAwaiter().GetResult();
                var match = Regex.Match(ioregOutput.StandardOutput, "IOPlatformUUID\" = \"(.*?)\"");
                if (match.Success)
                    hwid = match.Groups[1].Value;
                else
                    throw new InvalidOperationException("Failed to get HWID");
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error while getting HWID");
            Log.Warning("Using InstallationId as fallback");
            throw;
        }

        var bytes = Encoding.UTF8.GetBytes(hwid!);
        var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);

        return BitConverter.ToString(hash).Replace("-", "");
    }

    /// <summary>
    ///     Gets current system HWID (version compatible with Loader v2-3).
    /// </summary>
    /// <returns>HWID as <see cref="string" />.</returns>
    private static string GetHwidCompat()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new InvalidOperationException("Not supported on non-Windows platforms");
        var sb = new StringBuilder();

        var searcher = new ManagementObjectSearcher("root\\CIMV2",
            "SELECT * FROM Win32_Processor");

        foreach (var o in searcher.Get())
        {
            var queryObj = (ManagementObject) o;
            sb.Append(queryObj["NumberOfCores"]);
            sb.Append(queryObj["ProcessorId"]);
            sb.Append(queryObj["Name"]);
            sb.Append(queryObj["SocketDesignation"]);
        }

        searcher = new ManagementObjectSearcher("root\\CIMV2",
            "SELECT * FROM Win32_BIOS");

        foreach (var o in searcher.Get())
        {
            var queryObj = (ManagementObject) o;
            sb.Append(queryObj["Manufacturer"]);
            sb.Append(queryObj["Name"]);
            sb.Append(queryObj["Version"]);
        }

        searcher = new ManagementObjectSearcher("root\\CIMV2",
            "SELECT * FROM Win32_BaseBoard");

        foreach (var o in searcher.Get())
        {
            var queryObj = (ManagementObject) o;
            sb.Append(queryObj["Product"]);
        }

        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        var sha = SHA256.Create();

        var hash = sha.ComputeHash(bytes);

        return BitConverter.ToString(hash).Replace("-", "");
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
    
    public static string GetOsName() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" :
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "OSX" :
        "Unknown";

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
}