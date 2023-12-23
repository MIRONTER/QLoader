using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using QSideloader.Utilities;
using QSideloader.ViewModels;
using QSideloader.Views;
using ReactiveUI;
using Serilog;
using Splat;

namespace QSideloader;

public partial class App : Application
{
    public override void Initialize()
    {
        // Set current directory to app's directory
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        Name = Program.Name;

        // Force Russian locale (for testing)
        //Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("ru");

        if (!Design.IsDesignMode)
        {
            var sideloaderSettings = Globals.SideloaderSettings;
            if (sideloaderSettings.ForceEnglish)
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            LoggerHelper.InitializeLogging(sideloaderSettings);
        }
        
        Locator.CurrentMutable.RegisterViewsForViewModels(Assembly.GetExecutingAssembly());

        AvaloniaXamlLoader.Load(this);
        
        // Clean up leftovers from old versions
        Cleanup();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            desktop.MainWindow.DataContext = new MainWindowViewModel(mainWindow);
            Globals.MainWindowViewModel = (MainWindowViewModel) desktop.MainWindow.DataContext;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void Cleanup()
    {
        if (File.Exists("Avalonia.dll"))
            // we're unpacked, don't clean up
            return;
        
        // delete old directories
        string[] dirs = ["tools", "libvlc"];
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
                continue;
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception)
            {
                Log.Warning("Couldn't delete old {Dir} directory", dir);
            }
        }

        // don't delete library files on macOS
        // IncludeNativeLibrariesForSelfExtract doesn't seem to work properly for osx builds
        if (OperatingSystem.IsMacOS())
        {
            return;
        }
        
        // delete unpacked library files
        foreach (var file in Directory.GetFiles(Environment.CurrentDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(file => IsLibraryFileNameRegex().IsMatch(file)))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception)
            {
                Log.Warning("Couldn't delete old library file: {File}", file);
            }
        }
    }
    
    [GeneratedRegex(@"\.(dll|so|dylib)$")]
    private static partial Regex IsLibraryFileNameRegex();
}