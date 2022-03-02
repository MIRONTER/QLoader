using System;
using System.IO;
using CliWrap;
using CliWrap.Buffered;
using Serilog;

namespace QSideloader.Helpers;

public static class ZipUtil
{
    public static void ExtractArchive(string archivePath, string? extractPath = null)
    {
        if (!File.Exists(archivePath))
            throw new ArgumentException("Invalid archive path");
        if (string.IsNullOrEmpty(extractPath))
            extractPath = Path.GetDirectoryName(archivePath);
        Log.Debug("Extracting archive: \"{ArchivePath}\" -> \"{ExtractPath}\"",
            archivePath, extractPath);
        Cli.Wrap(PathHelper.SevenZipPath)
            .WithArguments($"x \"{archivePath}\" -y -aoa -o\"{extractPath}\"")
            .ExecuteBufferedAsync()
            .GetAwaiter().GetResult();
    }
}