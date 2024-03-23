using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace QSideloader.Utilities;

public static class PathHelper
{
    static PathHelper()
    {
        try
        {
            RclonePath = FindExecutable("FFA", "rclone");
        }
        catch
        {
            try
            {
                RclonePath = FindExecutable("rclone", "rclone");
            }
            catch
            {
                // DownloaderService will download rclone
                Directory.CreateDirectory(Path.Combine(Program.DataDirectory, "rclone"));
                RclonePath = OperatingSystem.IsWindows()
                    ? Path.Combine(Program.DataDirectory, "rclone", "FFA.exe")
                    : Path.Combine(Program.DataDirectory, "rclone", "FFA");
            }
        }
    }

    public static string AdbPath { get; } = FindExecutable("adb");
    public static string RclonePath { get; }
    public static string AaptPath { get; } = FindExecutable("aapt2");
    public static string? LibVlcPath { get; } = FindLibVlc();
    public static string SevenZipPath { get; } =
        OperatingSystem.IsWindows() ? FindExecutable("7za") : FindExecutable("7zz");
    public static string SettingsPath => Path.Combine(Program.DataDirectory, "settings.json");
    public static string OverridesPath => "overrides.conf";
    public static string ResourcesPath => Path.Combine(Program.DataDirectory, "Resources");
    public static string ThumbnailsPath => Path.Combine(ResourcesPath, "thumbnails");
    public static string TrailersPath => Path.Combine(ResourcesPath, "videos");
    public static string DefaultDownloadsPath => Path.Combine(Program.DataDirectory, "downloads");
    public static string DefaultBackupsPath => Path.Combine(Program.DataDirectory, "backups");

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

        if (foundFiles.Count > 0)
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

    private static string FindExecutable(string name, string? extraDir = null)
    {
        // Build a list of all the possible paths to check
        var possiblePaths = new List<string>
        {
            name, // Current directory
            Path.Combine(Program.DataDirectory, name)
        };

        if (!string.IsNullOrEmpty(extraDir))
        {
            possiblePaths.Add(Path.Combine(extraDir, name));
            possiblePaths.Add(Path.Combine(Program.DataDirectory, extraDir, name));
        }

        // Search NATIVE_DLL_SEARCH_DIRECTORIES if available
        name = OperatingSystem.IsWindows() ? name + ".exe" : name;
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeDllSearchDirectories)
        {
            var directories = nativeDllSearchDirectories.Split(Path.PathSeparator);
            possiblePaths.AddRange(directories.Select(dir => Path.Combine(dir, name)));
        }
        else
        {
            throw new InvalidOperationException("NATIVE_DLL_SEARCH_DIRECTORIES not set");
        }

        // Find the first valid executable within the list
        foreach (var path in possiblePaths.Where(File.Exists))
        {
            Log.Debug("Found {BinaryName} binary at {BinaryPath}", name, path);
            if (OperatingSystem.IsWindows()) 
                return path;

            GeneralUtils.TrySetExecutableBit(path);
            return path;
        }

        // If nothing found, throw the error
        throw new FileNotFoundException($"Could not find {name} in any possible path"); 
    }

    
    private static string? FindLibVlc()
    {
        var archName = Environment.Is64BitProcess ? "win-x64" : "win-x86";

        // Windows
        if (OperatingSystem.IsWindows())
        {
            if (Directory.Exists("libvlc")) 
                return Path.GetFullPath(Path.Combine("libvlc", archName));

            if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeDllSearchDirectories)
                return FindInDirectories("libvlc.dll", nativeDllSearchDirectories.Split(Path.PathSeparator)); 
        }

        // macOS
        else if (OperatingSystem.IsMacOS())
        {
            if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeDllSearchDirectories)
                return FindInDirectories("libvlc.dylib", nativeDllSearchDirectories.Split(Path.PathSeparator)); 
        }

        // Other (or failure)
        Log.Warning("Couldn't find libvlc");
        return null;

        string? FindInDirectories(string libName, string[] directories)
        {
            return directories
                       .Select(directory => Path.Combine(directory, "libvlc", archName)) // Prioritize pre-structured layout
                       .FirstOrDefault(File.Exists) ?? // Search win-x64/win-x86 within libvlc folder 
                   directories
                       .FirstOrDefault(x => File.Exists(Path.Combine(x, libName))); // Fallback
        }
    }
}