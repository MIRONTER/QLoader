using System;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Win32;
using Serilog;

namespace QSideloader.Common;

public static partial class Hwid
{
    /// <summary>
    ///     Gets current system HWID.
    /// </summary>
    /// <param name="useCompatOnWindows">Use <see cref="GetHwidCompat"/> if on Windows.</param>
    /// <returns>HWID as <see cref="string" />.</returns>
    public static string GetHwid(bool useCompatOnWindows)
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
                if (useCompatOnWindows)
                    return GetHwidCompat();
                var regKey =
                    Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false);
                var regValue = regKey?.GetValue("MachineGuid") ??
                               throw new InvalidOperationException("Failed to get HWID");
                hwid = regValue.ToString();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var ioregOutput = Cli.Wrap("ioreg")
                    .WithArguments("-rd1 -c IOPlatformExpertDevice")
                    .ExecuteBufferedAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                var match = IoPlatformUuidRegex().Match(ioregOutput.StandardOutput);
                if (match.Success)
                    hwid = match.Groups[1].Value;
                else
                    throw new InvalidOperationException("Failed to get HWID");
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error while getting HWID");
            Log.Warning("Using InstallationId as fallback");
            throw;
        }

        var bytes = Encoding.UTF8.GetBytes(hwid!);
        var hash = SHA256.HashData(bytes);

        return BitConverter.ToString(hash).Replace("-", "");
    }

    /// <summary>
    ///     Gets current system HWID (version compatible with Loader v2-3).
    /// </summary>
    /// <returns>HWID as <see cref="string" />.</returns>
    private static string GetHwidCompat()
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

        var hash = SHA256.HashData(bytes);

        return BitConverter.ToString(hash).Replace("-", "");
    }

    [GeneratedRegex("IOPlatformUUID\" = \"(.*?)\"")]
    private static partial Regex IoPlatformUuidRegex();
}