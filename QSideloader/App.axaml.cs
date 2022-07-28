using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Markup.Xaml;
using QSideloader.Helpers;
using QSideloader.Services;
using QSideloader.ViewModels;
using QSideloader.Views;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Exceptions;
using Serilog.Formatting.Compact;

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

            var trailersAddonPath = "";
            if (File.Exists("TrailersAddon.zip"))
                trailersAddonPath = "TrailersAddon.zip";
            if (File.Exists(Path.Combine("..", "TrailersAddon.zip")))
                trailersAddonPath = Path.Combine("..", "TrailersAddon.zip");

            if (!string.IsNullOrEmpty(trailersAddonPath))
            {
                Log.Information("Found trailers addon zip. Starting background install");
                Task.Run(async () => { await GeneralUtils.InstallTrailersAddonAsync(trailersAddonPath, true); });
            }
        }

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow();
            var notificationManager = new WindowNotificationManager(desktop.MainWindow)
            {
                Position = NotificationPosition.TopRight,
                MaxItems = 3
            };
            Globals.MainWindowViewModel = new MainWindowViewModel(notificationManager);
            desktop.MainWindow.DataContext = Globals.MainWindowViewModel;
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
        if (File.Exists(clefLogPath) && new FileInfo(clefLogPath).Length > 5000000)
            File.Delete(clefLogPath);
        if (File.Exists(exceptionsLogPath))// && new FileInfo(exceptionsLogPath).Length > 10000000)
            File.Delete(exceptionsLogPath);

        // Delete old log file with invalid format
        if (File.Exists("debug_log.json"))
            File.Delete("debug_log.json");

        var sideloaderSettings = Globals.SideloaderSettings;
        
        var executingAssembly = Assembly.GetExecutingAssembly();
        var programName = executingAssembly.GetName().Name;
        var versionString = executingAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "";
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "OSX" :
            "Unknown";
        GlobalLogContext.PushProperty("AppVersion", versionString);
        GlobalLogContext.PushProperty("OperatingSystem", os);
        GlobalLogContext.PushProperty("InstallationId", sideloaderSettings.InstallationId);

        var humanReadableLogger = new LoggerConfiguration().MinimumLevel.Verbose()
            .WriteTo.File(humanReadableLogPath)
            .CreateLogger();

        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .Enrich.WithThreadId().Enrich.WithThreadName()
            .Enrich.WithExceptionDetails()
            .Enrich.FromGlobalLogContext()
            .Enrich.FromLogContext()
            .WriteTo.Logger(humanReadableLogger)
            .WriteTo.File(new CompactJsonFormatter(), clefLogPath)
            .CreateLogger();

        LogStartMessage(Log.Logger, programName, versionString);

        SetExceptionLoggers();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && sideloaderSettings.EnableDebugConsole)
        {
            AllocConsole();
            Console.Title = $"{Assembly.GetExecutingAssembly().GetName().Name} debug console";
        }
#if DEBUG
        Console.Title = $"{Assembly.GetExecutingAssembly().GetName().Name} debug console";
#endif
        var consoleLogger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().CreateLogger();
        LogStartMessage(consoleLogger, programName, versionString);
        Log.CloseAndFlush();

        Logger? seqLogger = null;
        if (sideloaderSettings.EnableRemoteLogging)
        {
            var seqLevelSwitch = new LoggingLevelSwitch();
            seqLogger = new LoggerConfiguration().MinimumLevel.ControlledBy(seqLevelSwitch)
                .Enrich.With<PathsMaskingEnricher>()
                .WriteTo.Seq("https://qloader.5698452.xyz/seq-ingest/",
                    apiKey: "scSaNHjkaRxTtanoeVpJ",
                    controlLevelSwitch: seqLevelSwitch)
                .CreateLogger();
            LogStartMessage(seqLogger, programName, versionString);
        }
        
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .Enrich.WithThreadId().Enrich.WithThreadName()
            .Enrich.WithExceptionDetails()
            .Enrich.FromGlobalLogContext()
            .Enrich.FromLogContext()
            .WriteTo.Logger(humanReadableLogger)
            .WriteTo.Logger(seqLogger ?? Logger.None)
            .WriteTo.File(new CompactJsonFormatter(), clefLogPath)
            .WriteTo.Logger(consoleLogger)
            .CreateLogger();

        if (Debugger.IsAttached)
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (!ShouldLogFirstChanceException(e.Exception)) return;
                consoleLogger.Error(e.Exception, "FirstChanceException");
            };
    }

    private static void SetExceptionLoggers()
    {
        // Log all exceptions
        /*
        var firstChanceExceptionLogger = new LoggerConfiguration().MinimumLevel.Error()
            .WriteTo.File(exceptionsLogPath)
            .CreateLogger();
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (!ShouldLogFirstChanceException(e.Exception)) return;
            firstChanceExceptionLogger.Error(e.Exception, "FirstChanceException");
        };*/

        // Log unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = (Exception) e.ExceptionObject;
            if (e.IsTerminating)
            {
                Log.Fatal(exception, "------APPLICATION CRASH------");
                Log.CloseAndFlush();
            }
            else
                Log.Error(exception, "UnhandledException");
        };
    }

    private static bool ShouldLogFirstChanceException(Exception e)
    {
        return !((e.StackTrace is not null && e.StackTrace.Contains("GetRcloneDownloadStats"))
                 || e.Message.Contains($"127.0.0.1:{DownloaderService.RcloneStatsPort}")
                 || e.Message.Contains("does not contain a definition for 'bytes'")
                 || e.Message.Contains("does not contain a definition for 'speed'"));
    }
    private static void LogStartMessage(ILogger logger, string? programName, string versionString)
    {
        logger.Information("----------------------------------------");
        logger.Information("Starting {ProgramName} {VersionString}...",
            programName, versionString);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}