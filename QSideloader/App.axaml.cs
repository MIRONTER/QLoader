using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using QSideloader.Helpers;
using QSideloader.ViewModels;
using QSideloader.Views;
using Serilog;
using Serilog.Exceptions;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Json;

namespace QSideloader;

public class App : Application
{
    public override void Initialize()
    {
        var exePath = Path.GetDirectoryName(AppContext.BaseDirectory);
        if (exePath is not null)
            Directory.SetCurrentDirectory(exePath);

        if (!Design.IsDesignMode)
        {
            InitializeLogging();

            if (File.Exists("TrailersAddon.zip"))
                Task.Run(() =>
                {
                    Log.Information("Found trailers addon zip. Starting background install");
                    ZipUtil.ExtractArchiveAsync("TrailersAddon.zip", Directory.GetCurrentDirectory())
                        .GetAwaiter().GetResult();
                    Log.Information("Installed trailers addon");
                    File.Delete("TrailersAddon.zip");
                });
            if (File.Exists(Path.Combine("..", "TrailersAddon.zip")))
                Task.Run(() =>
                {
                    Log.Information("Found trailers addon zip. Starting background install");
                    ZipUtil.ExtractArchiveAsync(Path.Combine("..", "TrailersAddon.zip"),
                        Directory.GetCurrentDirectory()).GetAwaiter().GetResult();
                    Log.Information("Installed trailers addon");
                    File.Delete(Path.Combine("..", "TrailersAddon.zip"));
                });
        }

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            Globals.MainWindowViewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = Globals.MainWindowViewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void InitializeLogging()
    {
        const string humanReadableLogPath = "debug_log.txt";
        const string clefLogPath = "debug_log.clef";
        const string exceptionsLogPath = "debug_exceptions.txt";
        if (File.Exists(humanReadableLogPath) && new FileInfo(humanReadableLogPath).Length > 3000000)
            File.Delete(humanReadableLogPath);
        if (File.Exists("debug_log.json"))
            File.Delete("debug_log.json");
        if (File.Exists(clefLogPath) && new FileInfo(clefLogPath).Length > 5000000)
            File.Delete(clefLogPath);
        if (File.Exists(exceptionsLogPath) && new FileInfo(exceptionsLogPath).Length > 10000000)
            File.Delete(exceptionsLogPath);

        var humanReadableLogger = new LoggerConfiguration().MinimumLevel.Verbose()
            .WriteTo.File(humanReadableLogPath, fileSizeLimitBytes: 3000000)
            .CreateLogger();

        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .Enrich.WithThreadId().Enrich.WithThreadName()
            .Enrich.WithExceptionDetails()
            .WriteTo.Logger(humanReadableLogger)
            .WriteTo.File(new CompactJsonFormatter(), clefLogPath, fileSizeLimitBytes: 3000000)
            .CreateLogger();

        LogStartMessage(Log.Logger);

        // Log all exceptions
        var firstChanceExceptionLogger = new LoggerConfiguration().MinimumLevel.Error()
            .WriteTo.File(exceptionsLogPath)
            .CreateLogger();
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (!ShouldLogFirstChanceException(e.Exception)) return;
            firstChanceExceptionLogger.Error(e.Exception, "FirstChanceException");
        };
        
        // Log unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = (Exception) e.ExceptionObject;
            if (e.IsTerminating)
            {
                Log.Fatal("------APPLICATION CRASH------");
                Log.Fatal(exception, "UnhandledException");
                Log.CloseAndFlush();
            }
            else
                Log.Error(exception, "UnhandledException");
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Globals.SideloaderSettings.EnableDebugConsole)
        {
            AllocConsole();
            Console.Title = $"{Assembly.GetExecutingAssembly().GetName().Name} debug console";
        }
#if DEBUG
        Console.Title = $"{Assembly.GetExecutingAssembly().GetName().Name} debug console";
#endif
        var consoleLogger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().CreateLogger();
        LogStartMessage(consoleLogger);
        Log.CloseAndFlush();
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .Enrich.WithThreadId().Enrich.WithThreadName()
            .Enrich.WithExceptionDetails()
            .WriteTo.Logger(humanReadableLogger)
            .WriteTo.File(new CompactJsonFormatter(), clefLogPath, fileSizeLimitBytes: 3000000)
            .WriteTo.Logger(consoleLogger)
            .CreateLogger();

        if (Debugger.IsAttached)
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (!ShouldLogFirstChanceException(e.Exception)) return;
                consoleLogger.Error(e.Exception, "FirstChanceException");
            };

        bool ShouldLogFirstChanceException(Exception e)
        {
            return !((e.StackTrace is not null && e.StackTrace.Contains("GetRcloneDownloadStats"))
                     || e.Message.Contains("127.0.0.1:48040")
                     || e.Message.Contains("does not contain a definition for 'bytes'")
                     || e.Message.Contains("does not contain a definition for 'speed'"));
        }
    }

    private static void LogStartMessage(ILogger logger)
    {
        var executingAssembly = Assembly.GetExecutingAssembly();
        var programName = executingAssembly.GetName().Name;
        var versionString = executingAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "";
        logger.Information("----------------------------------------");
        logger.Information("Starting {ProgramName} {VersionString}...",
            programName, versionString);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}