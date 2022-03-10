using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.CSharp.RuntimeBinder;
using QSideloader.Helpers;
using QSideloader.ViewModels;
using QSideloader.Views;
using Serilog;
using Serilog.Exceptions;
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
            InitializeLogging();

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
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
        const string jsonLogPath = "debug_log.json";
        const string exceptionsLogPath = "debug_exceptions.txt";
        if (File.Exists(humanReadableLogPath) && new FileInfo(humanReadableLogPath).Length > 3000000)
            File.Delete(humanReadableLogPath);
        if (File.Exists(jsonLogPath) && new FileInfo(jsonLogPath).Length > 5000000)
            File.Delete(jsonLogPath);
        
        var humanReadableLogger = new LoggerConfiguration().MinimumLevel.Debug()
            .WriteTo.File(humanReadableLogPath, fileSizeLimitBytes: 3000000)
            .CreateLogger();
        
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug()
            .Enrich.WithThreadId().Enrich.WithThreadName()
            .Enrich.WithExceptionDetails()
            .WriteTo.Logger(humanReadableLogger)
            .WriteTo.Debug(
                outputTemplate:
                "{Exception} {Properties:j}{NewLine}{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}")
            .WriteTo.File(new JsonFormatter(renderMessage: true), jsonLogPath, fileSizeLimitBytes: 3000000)
            .CreateLogger();
        
        LogStartMessage(Log.Logger);

        // Log all exceptions
        if (File.Exists(exceptionsLogPath))
            File.Delete(exceptionsLogPath);
        var firstChanceExceptionHandler = new LoggerConfiguration().MinimumLevel.Error()
            .WriteTo.File(exceptionsLogPath)
            .CreateLogger();
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception.StackTrace is not null && e.Exception.StackTrace.Contains("GetRcloneDownloadStats")
                || e.Exception.Message.Contains("127.0.0.1:5572")
                || e.Exception.Message.Contains("does not contain a definition for 'bytes'")
                || e.Exception.Message.Contains("does not contain a definition for 'speed'")) return;
            firstChanceExceptionHandler.Error(e.Exception, "FirstChanceException");
        };

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            !Globals.SideloaderSettings.EnableDebugConsole) return;
        AllocConsole();
        Console.Title = $"{Assembly.GetExecutingAssembly().GetName().Name} debug console";
        var consoleLogger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().CreateLogger();
        LogStartMessage(consoleLogger);
        Log.CloseAndFlush();
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug()
            .Enrich.WithThreadId().Enrich.WithThreadName()
            .Enrich.WithExceptionDetails()
            .WriteTo.Logger(humanReadableLogger)
            .WriteTo.Debug(
                outputTemplate:
                "{Exception} {Properties:j}{NewLine}{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}")
            .WriteTo.File(new JsonFormatter(renderMessage: true), jsonLogPath, fileSizeLimitBytes: 3000000)
            .WriteTo.Logger(consoleLogger)
            .CreateLogger();
    }

    private static void LogStartMessage(ILogger logger)
    {
        logger.Information("----------------------------------------");
        logger.Information("Starting {ProgramName}...",
            Assembly.GetExecutingAssembly().GetName().Name);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}