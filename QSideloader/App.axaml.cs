using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Markup.Xaml;
using QSideloader.Utilities;
using QSideloader.ViewModels;
using QSideloader.Views;

namespace QSideloader;

public class App : Application
{
    public override void Initialize()
    {
        // Set current directory to app's directory
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        Name = Program.Name;

        // Force Russian locale
        //Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("ru");

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
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            var notificationManager = new WindowNotificationManager(desktop.MainWindow)
            {
                Position = NotificationPosition.TopRight,
                MaxItems = 3
            };
            Globals.MainWindowViewModel = new MainWindowViewModel(mainWindow, notificationManager);
            desktop.MainWindow.DataContext = Globals.MainWindowViewModel;
        }

        base.OnFrameworkInitializationCompleted();
    }
}