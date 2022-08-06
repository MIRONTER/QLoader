using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ApkNet.ApkReader;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Win32;
using QSideloader.ViewModels;
using Serilog;
using SerilogTimings;
using ApkInfo = QSideloader.Models.ApkInfo;

namespace QSideloader.Utilities;

public static class GeneralUtils
{
    public static string GetFileChecksum(HashingAlgoTypes hashingAlgoType, string filename)
    {
        using var hasher = HashAlgorithm.Create(hashingAlgoType.ToString()) ??
                           throw new ArgumentException($"{hashingAlgoType.ToString()} is not a valid hash algorithm");
        using var stream = File.OpenRead(filename);
        var hash = hasher.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "");
    }

    public static async Task<ApkInfo> GetApkInfoAsync(string apkPath)
    {
        /*if (!File.Exists(apkPath))
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
        return apkInfo;*/

        using var apkArchive = ZipFile.OpenRead(apkPath);
        byte[]? manifestData = null;
        byte[]? resourcesData = null;
        foreach (var entry in apkArchive.Entries)
        {
            switch (entry.Name)
            {
                case "AndroidManifest.xml":
                {
                    manifestData = new byte[entry.Length];
                    await using var stream = entry.Open();
                    var length = await stream.ReadAsync(manifestData.AsMemory(0, (int) entry.Length));
                    if (length != entry.Length)
                        throw new Exception("Failed to read AndroidManifest.xml");
                    break;
                }
                case "resources.arsc":
                {
                    resourcesData = new byte[entry.Length];
                    await using var stream = entry.Open();
                    var length = await stream.ReadAsync(resourcesData.AsMemory(0, (int) entry.Length));
                    if (length != entry.Length)
                        throw new Exception("Failed to read resources.arsc");
                    break;
                }
            }
        }
        if (manifestData is null)
            throw new Exception("AndroidManifest.xml not found in APK");
        if (resourcesData is null)
            throw new Exception("resources.arsc not found in APK");

        var apkReader = new ApkReader();
        var info = apkReader.extractInfo(manifestData, resourcesData);
        return new ApkInfo
        {
            ApplicationLabel = info.label,
            PackageName = info.packageName,
            VersionCode = int.Parse(info.versionCode),
            VersionName = info.versionName
        };
    }

    /// <summary>
    ///     Gets current system HWID.
    /// </summary>
    /// <returns>HWID as <see cref="string" />.</returns>
    public static string GetHwid()
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
                var regKey =
                    Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography", false);
                var regValue = regKey?.GetValue("MachineGuid");
                if (regValue != null)
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
                    Log.Warning("Could not get HWID from ioreg");
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error while getting HWID");
            Log.Warning("Using InstallationId as fallback");
            return GetHwidFallback();
        }

        if (hwid is null)
        {
            Log.Warning("Failed to get HWID. Using InstallationId as fallback");
            return GetHwidFallback();
        }

        var bytes = Encoding.UTF8.GetBytes(hwid);
        var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);

        return BitConverter.ToString(hash).Replace("-", "");
    }

    /// <summary>
    ///     Gets fallback HWID replacement derived from current <see cref="SideloaderSettingsViewModel.InstallationId" />.
    /// </summary>
    /// <returns>HWID as <see cref="string" />.</returns>
    private static string GetHwidFallback()
    {
        var installationId = Globals.SideloaderSettings.InstallationId.ToString();
        var bytes = Encoding.UTF8.GetBytes(installationId);
        var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "");
    }

    public static async Task InstallTrailersAddonAsync(string path, bool delete)
    {
        using var op = Operation.Begin("Installing trailers addon");
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
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
public enum HashingAlgoTypes
{
    MD5,
    SHA1,
    SHA256,
    SHA384,
    SHA512
}