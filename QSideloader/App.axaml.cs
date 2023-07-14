using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using QSideloader.Utilities;
using QSideloader.ViewModels;
using QSideloader.Views;
using ReactiveUI;
using Splat;

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
        
        Locator.CurrentMutable.RegisterViewsForViewModels(Assembly.GetCallingAssembly());

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            desktop.MainWindow.DataContext = new MainWindowViewModel(mainWindow);
            Globals.MainWindowViewModel = (MainWindowViewModel)desktop.MainWindow.DataContext;
        }

        base.OnFrameworkInitializationCompleted();
    }
}