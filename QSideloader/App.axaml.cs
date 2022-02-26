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
        AvaloniaXamlLoader.Load(this);

        var exePath = Path.GetDirectoryName(AppContext.BaseDirectory);
        if (exePath is not null)
            Directory.SetCurrentDirectory(exePath);

        if (!Design.IsDesignMode)
            InitializeLogging();

        Log.Information("----------------------------------------");
        Log.Information("Starting {ProgramName}...",
            Assembly.GetExecutingAssembly().GetName().Name);
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
        if (File.Exists("debug_log.txt") && new FileInfo("debug_log.txt").Length > 3000000)
            File.Delete("debug_log.txt");
        if (File.Exists("debug_log.json") && new FileInfo("debug_log.json").Length > 5000000)
            File.Delete("debug_log.json");


        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Globals.SideloaderSettings.EnableDebugConsole)
        {
            AllocConsole();
            Console.Title = $"{Assembly.GetExecutingAssembly().GetName().Name} debug console";
        }

        var humanReadableLogger = new LoggerConfiguration().MinimumLevel.Debug()
            .WriteTo.File("debug_log.txt", fileSizeLimitBytes: 3000000)
            .CreateLogger();

        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug()
            .Enrich.WithThreadId().Enrich.WithThreadName()
            .Enrich.WithExceptionDetails()
            .WriteTo.Logger(humanReadableLogger)
            .WriteTo.Debug(
                outputTemplate:
                "{Exception} {Properties:j}{NewLine}{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}")
            .WriteTo.File(new JsonFormatter(renderMessage: true), "debug_log.json", fileSizeLimitBytes: 3000000)
            .WriteTo.Console()
            .CreateLogger();

        // Log all exceptions
        if (File.Exists("debug_exceptions.txt"))
            File.Delete("debug_exceptions.txt");
        var firstChanceExceptionHandler = new LoggerConfiguration().MinimumLevel.Error()
            .WriteTo.File("debug_exceptions.txt")
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
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}