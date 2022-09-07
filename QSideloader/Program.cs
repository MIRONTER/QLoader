using System;
using System.Threading;
using Avalonia;
using Avalonia.ReactiveUI;

namespace QSideloader;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "QLoaderMutex", out var createdNew);
        if (!createdNew)
        {
            Console.Out.Write("Loader is already running, exiting...");
            Environment.Exit(1);
        }
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    private static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI()
            //.UseSkia()
            .With(new Win32PlatformOptions {UseWindowsUIComposition = true});

        return builder;
    }
}