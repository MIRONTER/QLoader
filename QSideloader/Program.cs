using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.ReactiveUI;
using Avalonia.Rendering;

namespace QSideloader;

internal class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI()
            .UseSkia();

        // Animations lag fix for Windows. Source: https://github.com/AvaloniaUI/Avalonia/issues/2945#issuecomment-534892298
        if (Environment.OSVersion.Platform == PlatformID.Win32NT && false)
            if (DwmIsCompositionEnabled(out var dwmEnabled) == 0 && dwmEnabled)
            {
                var wp = builder.WindowingSubsystemInitializer;
                return builder.UseWindowingSubsystem(() =>
                {
                    wp();
                    AvaloniaLocator.CurrentMutable.Bind<IRenderTimer>().ToConstant(new WindowsDWMRenderTimer());
                });
            }
        // Possible fix for Linux too. Source: https://github.com/AvaloniaUI/Avalonia/issues/2945#issuecomment-543800104
        // Do not use. Breaks rendering
        /*else
        {
            var wp = builder.WindowingSubsystemInitializer;
            return builder.UseWindowingSubsystem(() =>
            {
                wp();
                AvaloniaLocator.CurrentMutable.Bind<IRenderTimer>().ToConstant(new CustomRenderTimer());
            });
        }*/

        return builder;
    }

    [DllImport("Dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(out bool enabled);
}

// ReSharper disable once InconsistentNaming
internal class WindowsDWMRenderTimer : IRenderTimer
{
    public WindowsDWMRenderTimer()
    {
        var renderTick = new Thread(() =>
        {
            var sw = new Stopwatch();
            sw.Start();
            while (true)
            {
                DwmFlush();
                Tick?.Invoke(sw.Elapsed);
            }
            // ReSharper disable once FunctionNeverReturns
        })
        {
            IsBackground = true
        };
        renderTick.Start();
    }

    public event Action<TimeSpan>? Tick;

    [DllImport("Dwmapi.dll")]
    private static extern int DwmFlush();
}

internal class CustomRenderTimer : IRenderTimer
{
    public CustomRenderTimer()
    {
        var renderTick = new Thread(() =>
        {
            var sw = new Stopwatch();
            sw.Start();
            while (true) Tick?.Invoke(sw.Elapsed);
            // ReSharper disable once FunctionNeverReturns
        })
        {
            IsBackground = true
        };
        renderTick.Start();
    }

    public event Action<TimeSpan>? Tick;
}