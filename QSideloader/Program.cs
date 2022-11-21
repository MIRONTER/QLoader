using System;
using System.Linq;
using Avalonia;
using Avalonia.ReactiveUI;

namespace QSideloader;

internal static class Program
{
    private static bool _useGpuRendering;

    public static string Name => "QLoader";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        _useGpuRendering = !args.Contains("--disable-gpu");
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
            .With(new Win32PlatformOptions {UseWindowsUIComposition = true, AllowEglInitialization = _useGpuRendering})
            .With(new X11PlatformOptions {UseGpu = _useGpuRendering})
            .With(new AvaloniaNativePlatformOptions {UseGpu = _useGpuRendering});

        return builder;
    }
}