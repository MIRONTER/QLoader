using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace QSideloader.Helpers;

public static class PathHelper
{
    static PathHelper()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AdbPath = @".\tools\windows\platform-tools\adb.exe";
            RclonePath = @".\tools\windows\rclone\FFA.exe";
            SevenZipPath = Path.Combine(@".\tools\windows", Environment.Is64BitProcess ? "x64" : "x86", "7za.exe");
            AaptPath = @".\tools\windows\platform-tools\aapt2.exe";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            AdbPath = @"./tools/linux/platform-tools/adb";
            RclonePath = @"./tools/linux/rclone/FFA";
            SevenZipPath = @"./tools/linux/7zz";
            AaptPath = @"./tools/linux/platform-tools/aapt2";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            AdbPath = @"./tools/darwin/platform-tools/adb";
            RclonePath = @"./tools/darwin/rclone/FFA";
            RclonePath = Path.Combine("./tools/darwin/",
                RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64", "rclone/FFA");
            SevenZipPath = @"./tools/darwin/7zz";
            AaptPath = @"./tools/darwin/platform-tools/aapt2";
        }
    }

    public static string AdbPath { get; } = "";
    public static string RclonePath { get; } = "";
    public static string SevenZipPath { get; } = "";
    public static string AaptPath { get; } = "";
    public static string SettingsPath => "settings.json";
    public static string ThumbnailsPath => Path.Combine("Resources", "thumbnails");
    public static string TrailersPath => Path.Combine("Resources", "videos");
    public static string DefaultDownloadsPath => Path.Combine(Environment.CurrentDirectory, "downloads");
    public static string DefaultBackupsPath => Path.Combine(Environment.CurrentDirectory, "backups");

    // Source: https://stackoverflow.com/a/55480402
    public static string GetActualCaseForFileName(string pathAndFileName)
    {
        var directory = Path.GetDirectoryName(pathAndFileName) ??
                        throw new InvalidOperationException("Path is not valid");
        var pattern = Path.GetFileName(pathAndFileName);
        string resultFileName;

        // Enumerate all files in the directory, using the file name as a pattern
        // This will list all case variants of the filename even on file systems that
        // are case sensitive
        var options = new EnumerationOptions
        {
            MatchCasing = MatchCasing.CaseInsensitive
        };
        var foundFiles = Directory.EnumerateFiles(directory, pattern, options).ToList();

        if (foundFiles.Any())
        {
            if (foundFiles.Count > 1)
                // More than two files with the same name but different case spelling found
                throw new Exception("Ambiguous File reference for " + pathAndFileName);

            resultFileName = foundFiles.First();
        }
        else
        {
            throw new FileNotFoundException("File not found" + pathAndFileName, pathAndFileName);
        }

        return resultFileName;
    }
}