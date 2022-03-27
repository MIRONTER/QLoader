using System;
using System.IO;
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
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            AdbPath = @"./tools/linux/platform-tools/adb";
            RclonePath = @"./tools/linux/rclone/FFA";
            SevenZipPath = @"./tools/linux/7zz";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            AdbPath = @"./tools/darwin/platform-tools/adb";
            RclonePath = @"./tools/darwin/rclone/FFA";
            SevenZipPath = @"./tools/darwin/7zz";
        }
    }

    public static string AdbPath { get; } = "";
    public static string RclonePath { get; } = "";
    public static string SevenZipPath { get; } = "";
    public static string SettingsPath => "settings.json";
    public static string ThumbnailsPath => Path.Combine("Resources", "thumbnails");
    public static string TrailersPath => Path.Combine("Resources", "videos");
}