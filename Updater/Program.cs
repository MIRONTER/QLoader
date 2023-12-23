// QLoader auto updater

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using QSideloader.Common;


// Set current directory to app's directory
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

try
{
    var updateInfo = await UpdateInfo.GetInfoAsync();

    var exeName = OperatingSystem.IsWindows() ? "Loader.exe" : "Loader";

    if (File.Exists(exeName))
    {
        // wait for process to exit, kill if it takes too long
        var process = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName))
            .FirstOrDefault(p => p.MainModule?.FileName == Path.GetFullPath(exeName));
        if (process != null)
        {
            Console.WriteLine("Loader is running, waiting for exit...");
            var cts = new CancellationTokenSource();
            cts.CancelAfter(5000);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("Loader did not exit in time, killing...");
                process.Kill();
            }
        }

    }

    var latestVersion = updateInfo.GetLatestVersion(null);
    if (latestVersion == null)
    {
        Console.WriteLine("Latest version not found!");
        Quit();
    }
    Console.WriteLine($"Downloading Loader {latestVersion!.VersionString}...");

    var asset = latestVersion.GetAsset();
    if (asset is null)
    {
        Console.WriteLine("Download asset not found!");
        Quit();
    }

    using var httpClientHandler = new HttpClientHandler();
    httpClientHandler.Proxy = WebRequest.DefaultWebProxy;
    using var httpClient = new HttpClient(httpClientHandler);
    var response = await httpClient.GetAsync(asset!.Url, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    var outFileName = response.Content.Headers.ContentDisposition?.FileName ?? "Update.zip";
    await using var outStream = File.Create(outFileName);
    await using var inStream = await response.Content.ReadAsStreamAsync();
    await inStream.CopyToAsync(outStream);
    inStream.Close();
    outStream.Close();
    // check SHA256
    var expectedHash = asset.Sha256;
    var hashBytes = SHA256.HashData(File.ReadAllBytes(outFileName));
    var hashString = BitConverter.ToString(hashBytes).Replace("-", "");
    if (!string.Equals(expectedHash, hashString, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Hash mismatch, download corrupted!");
        Console.WriteLine($"Expected: {expectedHash}");
        Console.WriteLine($"Actual: {hashString}");
        File.Delete(outFileName);
        Quit();
    }
    Console.WriteLine("Download complete!");

    Console.WriteLine("Extracting...");
    // check if the archive contains a single directory, if so, extract it's contents to the current directory
    var zipFile = ZipFile.OpenRead(outFileName);
    var singleDirectory = zipFile.Entries.All(entry =>
        !entry.FullName.EndsWith(Path.DirectorySeparatorChar) &&
        !entry.FullName.EndsWith(Path.AltDirectorySeparatorChar) ||
        entry.FullName.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) != 1);

    if (singleDirectory)
    {
        // extract to temp directory
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        zipFile.ExtractToDirectory(tempDir);
        var dirPath = Directory.GetDirectories(tempDir, "*", SearchOption.AllDirectories).First();
        // create directories
        foreach (var dir in Directory.GetDirectories(dirPath, "*", SearchOption.AllDirectories))
        {
            var relativeDir = dir.Replace(dirPath + Path.DirectorySeparatorChar, "");
            Directory.CreateDirectory(relativeDir);
        }
        // move files to current directory
        foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
        {
            var relativeFile = file.Replace(dirPath + Path.DirectorySeparatorChar, "");
            File.Move(file,  relativeFile, true);
        }
        // delete temp directory
        Directory.Delete(tempDir, true);
    }
    else
    {
        // extract to current directory
        zipFile.ExtractToDirectory(AppContext.BaseDirectory, true);
    }
    zipFile.Dispose();
    File.Delete(outFileName);
    Console.WriteLine($"Extract complete! Launching {exeName}...");
    TrySetExecutableBit(exeName);
    Process.Start(exeName);
    await Task.Delay(5000);
}
catch (Exception e)
{
    PrintPadded("ERROR!");
    Console.WriteLine(e);
    PrintPadded();
    Quit();
}

return;


void Quit()
{
    Console.WriteLine("\nPress any key to exit...");
    Console.ReadKey();
    Environment.Exit(0);
}

void PrintPadded(string text = "")
{
    var lineLength = Console.WindowWidth;
    Console.WriteLine(text.PadLeft(lineLength / 2 + text.Length / 2, '-').PadRight(lineLength, '-'));
}

static void TrySetExecutableBit(string filePath)
{
    if (!File.Exists(filePath))
        throw new FileNotFoundException(filePath);
    if (OperatingSystem.IsWindows())
        return;
    try
    {
        var mode = File.GetUnixFileMode(filePath);
        if (mode.HasFlag(UnixFileMode.UserExecute))
            return;
        mode |= UnixFileMode.UserExecute;
        File.SetUnixFileMode(filePath, mode);
    }
    catch
    {
        // ignored
    }
}