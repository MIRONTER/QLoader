using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;

var hwid = GetHwid();
if (args.Length > 0 && args[0] == "--just-hwid")
{
    Console.WriteLine(hwid);
    return;
}

Console.WriteLine($"Your HWID: {hwid}\nPress any key to exit...");
Console.ReadKey();

static string GetHwid()
{
    string? hwid = null;
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        if (File.Exists("/var/lib/dbus/machine-id"))
            hwid = File.ReadAllText("/var/lib/dbus/machine-id");
        if (File.Exists("/etc/machine-id"))
            hwid = File.ReadAllText("/etc/machine-id");
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return GetHwidCompat();

    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        var ioregOutput = Cli.Wrap("ioreg")
            .WithArguments("-rd1 -c IOPlatformExpertDevice")
            .ExecuteBufferedAsync().GetAwaiter().GetResult();
        var match = Regex.Match(ioregOutput.StandardOutput, "IOPlatformUUID\" = \"(.*?)\"");
        if (match.Success)
            hwid = match.Groups[1].Value;
    }

    if (hwid is null) throw new InvalidOperationException("Failed to get HWID");

    var bytes = Encoding.UTF8.GetBytes(hwid);
    var sha256 = SHA256.Create();
    var hash = sha256.ComputeHash(bytes);

    return BitConverter.ToString(hash).Replace("-", "");
}

static string GetHwidCompat()
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