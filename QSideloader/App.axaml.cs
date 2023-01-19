using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Markup.Xaml;
using QSideloader.Services;
using QSideloader.Utilities;
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
        Directory.SetCurrentDirectory(Path.GetDirectoryName(AppContext.BaseDirectory)!);
        Name = Program.Name;

        if (!Design.IsDesignMode)
        {
            var sideloaderSettings = Globals.SideloaderSettings;
            if (sideloaderSettings.ForceEnglish)
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            InitializeLogging(sideloaderSettings);
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

    private static void InitializeLogging(SideloaderSettingsViewModel sideloaderSettings)
    {
        const string humanReadableLogPath = "debug_log.txt";
        const string clefLogPath = "debug_log.clef";
        const string exceptionsLogPath = "debug_exceptions.txt";
        if (File.Exists(humanReadableLogPath) && new FileInfo(humanReadableLogPath).Length > 5000000)
            File.Delete(humanReadableLogPath);
        if (File.Exists(clefLogPath) && new FileInfo(clefLogPath).Length > 10000000)
            File.Delete(clefLogPath);
        if (File.Exists(exceptionsLogPath)) // && new FileInfo(exceptionsLogPath).Length > 10000000)
            File.Delete(exceptionsLogPath);

        // Delete old log file with invalid format
        if (File.Exists("debug_log.json"))
            File.Delete("debug_log.json");

        var executingAssembly = Assembly.GetExecutingAssembly();
        var programName = Program.Name;
        var versionString = executingAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "";
        GlobalLogContext.PushProperty("AppVersion", versionString);
        GlobalLogContext.PushProperty("OperatingSystem", GeneralUtils.GetOsName());
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
            // Disabling Quick Edit mode disables text selection and scrolling in the console window, so not doing that
            ConsoleHelper.AllocateConsole(false);

        try
        {
            Console.Title = $"{Program.Name} debug console";
        }
        catch
        {
            // ignored
        }

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
            {
                Log.Error(exception, "UnhandledException");
            }
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
}