using System;
using System.Linq;
using Avalonia;
using Avalonia.ReactiveUI;

namespace QSideloader;

internal static class Program
{
    private static bool _disableGpuRendering;

    public static string Name => "QLoader";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        _disableGpuRendering = args.Contains("--disable-gpu");
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    private static AppBuilder BuildAvaloniaApp()
    {
        var win32Options = new Win32PlatformOptions();
        var x11Options = new X11PlatformOptions();
        var avaloniaNativeOptions = new AvaloniaNativePlatformOptions();
        if (_disableGpuRendering)
        {
            win32Options.RenderingMode = new[] {Win32RenderingMode.Software};
            x11Options.RenderingMode = new[] {X11RenderingMode.Software};
            avaloniaNativeOptions.RenderingMode = new[] {AvaloniaNativeRenderingMode.Software};
        }

        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI()
            //.UseSkia()
            .With(win32Options)
            .With(x11Options)
            .With(avaloniaNativeOptions);

        return builder;
    }
}