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
            LoggerHelper.InitializeLogging(sideloaderSettings);
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
}