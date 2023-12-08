using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Serilog;

namespace QSideloader.Utilities;

public static class PathHelper
{
    static PathHelper()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // ReSharper disable StringLiteralTypo
            RclonePath = @".\tools\windows\rclone\FFA.exe";
            // ReSharper restore StringLiteralTypo
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var architectureString = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new NotImplementedException("Unsupported architecture")
            };
            RclonePath = Path.Combine("./tools/linux/", architectureString, "rclone/FFA");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var architectureString = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new NotImplementedException("Unsupported architecture")
            };
            RclonePath = Path.Combine("./tools/darwin/", architectureString, "rclone/FFA");
        }
    }

    public static string AdbPath { get; } = FindExecutable("adb");
    public static string RclonePath { get; } = "";
    public static string AaptPath { get; } = FindExecutable("aapt2");
    public static string SevenZipPath { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? FindExecutable("7za")
        : FindExecutable("7zz");
    public static string SettingsPath => "settings.json";
    public static string OverridesPath => "overrides.conf";
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
            throw new FileNotFoundException("File not found " + pathAndFileName, pathAndFileName);
        }

        return resultFileName;
    }

    private static string FindExecutable(string name)
    {
        // try current directory first
        if (File.Exists(name))
            return Path.GetFullPath(name);

        // search in NATIVE_DLL_SEARCH_DIRECTORIES
        name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? name + ".exe" : name;
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeDllSearchDirectories)
        {
            var directories = nativeDllSearchDirectories.Split(Path.PathSeparator);
            foreach (var directory in directories)
            {
                var exePath = Path.Combine(directory, name);
                if (File.Exists(exePath))
                    return exePath;
            }
        }
        else
        {
            throw new InvalidOperationException("NATIVE_DLL_SEARCH_DIRECTORIES not set");
        }

        // something went wrong (packaging error?)
        throw new FileNotFoundException($"Could not find {name} in NATIVE_DLL_SEARCH_DIRECTORIES");
    }
}