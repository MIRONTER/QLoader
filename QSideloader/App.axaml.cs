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
            if (e.Exception is SocketException or HttpRequestException &&
                (e.Exception.Message.Contains("Connection refused") ||
                 e.Exception.Message.Contains("Connection reset by peer") ||
                 e.Exception.Message.Contains("An error occurred while sending the request"))
                || e.Exception is RuntimeBinderException &&
                e.Exception.Message.Contains("Newtonsoft.Json.Linq.JObject")
                || e.Exception is TaskCanceledException or OperationCanceledException) return;
            firstChanceExceptionHandler.Error(e.Exception, "FirstChanceException");
        };

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            !Globals.SideloaderSettings.EnableDebugConsole) return;
        AllocConsole();
        Console.Title = $"{Assembly.GetExecutingAssembly().GetName().Name} debug console";
        var consoleLogger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().CreateLogger();
        LogStartMessage(consoleLogger);
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