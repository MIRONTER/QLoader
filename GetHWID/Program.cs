using System.Runtime.InteropServices;
using QSideloader.Utilities;

var hwid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? GeneralUtils.GetHwidCompat() : GeneralUtils.GetHwid();
Console.WriteLine($"Your HWID: {hwid}\nPress any key to exit...");
Console.ReadKey();
