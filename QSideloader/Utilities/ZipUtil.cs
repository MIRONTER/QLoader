
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace QSideloader.Utilities;

public static class ZipUtil
{
    public static async Task ExtractArchiveAsync(string archivePath, string? extractPath = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive not found", archivePath);
        if (string.IsNullOrEmpty(extractPath))
            extractPath = Path.GetDirectoryName(archivePath);
        if (!Directory.Exists(extractPath))
            throw new DirectoryNotFoundException("Extract path directory does not exist");
        Log.Debug("Extracting archive: \"{ArchivePath}\" -> \"{ExtractPath}\"",
            archivePath, extractPath);
        await Task.Run(() => ExtractArchiveInternal(archivePath, extractPath, ct), ct);
    }

    public static async Task<string> CreateArchiveAsync(string sourcePath, string destinationPath, string archiveName,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException("Invalid source path");
        if (!Directory.Exists(destinationPath))
            throw new DirectoryNotFoundException("Invalid destination path");
        var archivePath = Path.Combine(destinationPath, archiveName);
        Log.Debug("Creating archive: \"{SourcePath}\" -> \"{ArchivePath}\"",
            sourcePath, archivePath);
        if (File.Exists(archivePath))
            File.Delete(archivePath);
        sourcePath = Path.GetFullPath(sourcePath) + Path.DirectorySeparatorChar;
        await Task.Run(() => CreateArchiveInternal(sourcePath, archivePath), ct);
        return archivePath;
    }
    
    private static void ExtractArchiveInternal(string archivePath, string extractPath, CancellationToken ct)
    {
        using var archive = ArchiveFactory.Open(archivePath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.IsDirectory)
            {
                entry.WriteToDirectory(extractPath, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
            }
        }
    }
    
    private static void CreateArchiveInternal(string sourcePath, string archivePath)
    {
        using var stream = File.OpenWrite(archivePath);
        using var writer = WriterFactory.Open(stream, ArchiveType.Zip, CompressionType.Deflate);
        writer.WriteAll(sourcePath, "*", SearchOption.AllDirectories);
    }
}