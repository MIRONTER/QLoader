// QLoader auto updater

using System.Text.Json;
using QSideloader.Common;


// Set current directory to app's directory
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

try
{
    var a = await UpdateInfo.GetInfoAsync();

    Console.WriteLine(JsonSerializer.Serialize(a, CommonJsonSerializerContext.Default.UpdateInfo));
    Console.ReadKey();
    return;

    var exeName = OperatingSystem.IsWindows() ? "Loader.exe" : "Loader";

    if (!File.Exists(exeName))
    {
        Console.WriteLine($"Error: {exeName} not found!\nPress any key to exit...");
        Console.ReadKey();
        return;
    }
}
catch (Exception e)
{
    Console.WriteLine($"{e}\n\nPress any key to exit...");
    Console.ReadKey();
}


