// QLoader auto updater

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using QSideloader.Common;


// Set current directory to app's directory
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

try
{
    var updateInfo = await UpdateInfo.GetInfoAsync(GetUpdateUrlOverride());

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
        Finish();
    }
    Console.WriteLine($"Downloading Loader {latestVersion!.VersionString}...");

    var asset = latestVersion.GetAsset();
    if (asset is null)
    {
        Console.WriteLine("Download asset not found!");
        Finish();
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
        // Should we delete the file?
        //File.Delete(outFileName);
        Finish();
    }
    Console.WriteLine("Download complete!");

    Console.WriteLine("Extracting...");
    // check if the archive contains a single directory, if so, extract its contents to the current directory
    var zipFile = ZipFile.OpenRead(outFileName);
    var singleDirectory = zipFile.Entries.Count(entry => entry.FullName.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) == 1) == 1 &&
                          zipFile.Entries.All(entry => entry.FullName.Any(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar));
    Console.WriteLine($"Single directory: {singleDirectory}");

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
    // We need to dispose the ZipFile manually to close the file before we delete it
    zipFile.Dispose();
    File.Delete(outFileName);
    Console.WriteLine($"Extract complete! Launching {exeName}...");
    CommonUtils.TrySetExecutableBit(exeName);
    Process.Start(exeName);
    await Task.Delay(5000);
}
catch (Exception e)
{
    PrintPadded("ERROR!");
    Console.WriteLine(e);
    PrintPadded();
    Finish();
}

return;


string? GetUpdateUrlOverride()
{
    var overridesPath = Path.Combine(AppContext.BaseDirectory, "overrides.conf");
    if (!File.Exists(overridesPath))
        return null;
    var overrides = File.ReadAllLines(overridesPath);
    var updateUrlOverride = overrides.FirstOrDefault(x => x.StartsWith("UpdateInfoUrl="))?.Split('=')[1];
    if (string.IsNullOrWhiteSpace(updateUrlOverride))
        return null;
    Console.WriteLine($"Using update info url: {updateUrlOverride}");
    return updateUrlOverride;
}

void Finish()
{
    Console.WriteLine("\nPress any key to exit...");
    Console.ReadKey();
    Environment.Exit(1);
}

void PrintPadded(string text = "")
{
    var lineLength = Console.WindowWidth;
    Console.WriteLine(text.PadLeft(lineLength / 2 + text.Length / 2, '-').PadRight(lineLength, '-'));
}
