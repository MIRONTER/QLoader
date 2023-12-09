using QSideloader.Common;

var hwid = Hwid.GetHwid(true);
if (args.Length > 0 && args[0] == "--just-hwid")
{
    Console.WriteLine(hwid);
    return;
}

Console.WriteLine($"Your HWID: {hwid}\nPress any key to exit...");
Console.ReadKey();