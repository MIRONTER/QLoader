using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.ReactiveUI;
using Avalonia.Styling;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Navigation;
using QSideloader.Utilities;
using QSideloader.ViewModels;
using ReactiveUI;
using Serilog;

namespace QSideloader.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>, IMainWindow
{
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        Title = Program.Name;

        var thm = Application.Current?.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();
        if (thm is not null)
        {
            thm.PreferSystemTheme = false;
            thm.PreferUserAccentColor = true;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                thm.ForceWin32WindowToTheme(this, ThemeVariant.Dark);
        }

        AddHandler(DragDrop.DragEnterEvent, DragEnter);
        AddHandler(DragDrop.DragLeaveEvent, DragLeave);
        AddHandler(DragDrop.DropEvent, Drop);

        ContentFrame.NavigationFailed += ContentFrame_OnNavigationFailed;
        
        // Navigate to the first page
        NavigationView.SelectedItem = NavigationView.MenuItems.OfType<NavigationViewItem>().First();
        
        // Recalculate task list height when windows size changes
        this.GetObservable(ClientSizeProperty).Throttle(TimeSpan.FromMilliseconds(100))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RecalculateTaskListBoxHeight());
    }

    private INotificationManager? NotificationManager { get; set; }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void NavigationView_OnSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.IsSettingsSelected)
        {
            const string pageName = "QSideloader.Views.Pages.SettingsView";
            var pageType = Type.GetType(pageName);
            if (pageType is null) return;
            Log.Debug("Navigating to {View}", "SettingsView");
            ContentFrame.BackStack.Clear();
            ContentFrame.Navigate(pageType);
        }
        else
        {
            var selectedItem = (NavigationViewItem)e.SelectedItem;
            var selectedItemTag = (string)selectedItem.Tag!;
            var pageName = "QSideloader.Views.Pages." + selectedItemTag;
            var pageType = Type.GetType(pageName);
            if (pageType is null) return;
            Log.Debug("Navigating to {View}", selectedItemTag);
            ContentFrame.BackStack.Clear();
            ContentFrame.Navigate(pageType);
        }
    }

    private static void ContentFrame_OnNavigationFailed(object? sender, NavigationFailedEventArgs e)
    {
        Log.Error(e.Exception, "Failed to navigate to {View}", e.SourcePageType);
    }

    public void NavigateToGameDonationView()
    {
        NavigationView.SelectedItem = NavigationView.MenuItems
            .OfType<NavigationViewItem>()
            .First(x => (string?)x.Tag == "GameDonationView");
    }

    private void RecalculateTaskListBoxHeight()
    {
        var windowHeight = ClientSize.Height;
        TaskListBox.MaxHeight = (int)windowHeight / (double)3 / 60 * 60;
        //Log.Debug("Recalculated TaskListBox height to {Height}", taskListBox.MaxHeight);
    }

    private void TaskListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var viewModel = (MainWindowViewModel)DataContext!;
        var listBox = (ListBox?)sender;
        var selectedTask = (TaskViewModel?)e.AddedItems[0];
        if (listBox is null || selectedTask is null) return;
        listBox.SelectedItem = null;
        switch (selectedTask.IsFinished)
        {
            case true when viewModel.TaskList.Contains(selectedTask):
                Log.Debug("Dismissed finished task {TaskId} {TaskType} {TaskName}", selectedTask.TaskId,
                    selectedTask.TaskType, selectedTask.TaskName);
                viewModel.TaskList.Remove(selectedTask);
                break;
            case false:
                selectedTask.Cancel();
                break;
        }
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void Window_OnOpened(object? sender, EventArgs e)
    {
        // Couldn't set this in styles, so this will have to do
        NavigationView.SettingsItem.Content = Properties.Resources.Settings;

        if (Design.IsDesignMode) return;
        InitializeUpdater();
        RecalculateTaskListBoxHeight();
    }

    private void InitializeUpdater()
    {
        if (Globals.Overrides.TryGetValue("DisableSelfUpdate", out var value) && 
            bool.TryParse(value, out var disableSelfUpdate) && disableSelfUpdate ||
            Globals.Overrides["DisableSelfUpdate"] == "1")
        {
            Log.Warning("Updater disabled by override");
            return;
        }
        
        // TODO: add windows support
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Log.Warning("Running on Windows, skipping updater initialization");
            return;
        }

        var appcastUrl = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 or Architecture.X86 =>
                "https://qloader.5698452.xyz/files/appcast.xml",
            Architecture.Arm64 => "https://qloader.5698452.xyz/files/appcast_arm64.xml",
            _ => ""
        };
        if (string.IsNullOrEmpty(appcastUrl))
        {
            Log.Warning("Architecture {Architecture} is not supported by updater",
                RuntimeInformation.ProcessArchitecture);
            return;
        }

        Log.Information("Initializing updater");
        try
        {
            throw new NotImplementedException();
            //Globals.Updater = 
            //if (_sideloaderSettings.CheckUpdatesAutomatically)
            //    Globals.Updater.StartLoop(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize updater");
        }
    }

    // When new task is added, scroll to last task in the list
    /* this is hacky, consider just using regular ordering
    private void TaskListOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // ReSharper disable once InvertIf
        if (e.OldItems is not null && e.NewItems is not null && e.OldItems.Count > e.NewItems.Count ||
            e.OldItems is null && e.NewItems is not null)
        {
            var taskListBox = this.FindControl<ListBox>("TaskListBox");
            taskListBox.ScrollIntoView(taskListBox.Items.OfType<TaskView>().Last());
        }
    }*/
    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    [SuppressMessage("ReSharper", "SuggestBaseTypeForParameter")]
    private async void Window_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isClosing || ViewModel!.TaskList.Count == 0 || ViewModel.TaskList.All(x => x.IsFinished))
        {
            Log.Information("Closing application");
            await Log.CloseAndFlushAsync();
            return;
        }

        e.Cancel = true;
        Log.Information("Application close requested, cancelling tasks");
        foreach (var task in ViewModel.TaskList)
            task.Cancel();
        // Give tasks time to cancel
        // Check every 100ms for tasks to finish with a timeout of 2s
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            if (ViewModel.TaskList.All(x => x.IsFinished))
                break;
        }

        _isClosing = true;
        Close();
    }

    private void DragEnter(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDropPanel.IsVisible = true;
        e.Handled = true;
    }

    private void DragLeave(object? sender, RoutedEventArgs e)
    {
        DragDropPanel.IsVisible = false;
        e.Handled = true;
    }

    private async void Drop(object? sender, DragEventArgs e)
    {
        Log.Debug("DragDrop.Drop event");
        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles()?.ToList();
            if (files is null)
            {
                Log.Warning("e.Data.GetFileNames() returned null");
                return;
            }
            var fileNames = files.Select(x => x.Path.LocalPath).ToList();

            Log.Debug("Dropped folders/files: {FilesNames}", fileNames);
            await ViewModel!.HandleDroppedItemsAsync(fileNames);
        }
        else
        {
            Log.Warning("Drop data does not contain file names");
        }

        DragDropPanel.IsVisible = false;
        e.Handled = true;
    }

    // ReSharper disable UnusedParameter.Local
    private void Window_OnLoaded(object? sender, RoutedEventArgs e)
    {
        NotificationManager = new WindowNotificationManager(GetTopLevel(this))
        {
            Position = NotificationPosition.TopRight,
            MaxItems = 3
        };
        if (ViewModel is null) return;
        ViewModel.NotificationManager = NotificationManager;
    }

    private async void Window_OnKeyDown(object? sender, KeyEventArgs e)
    {
        var openFile = e.Key == Key.F2;
        var openFolder = e.Key == Key.F3;
        if (!openFile && !openFolder) return;
        e.Handled = true;
        if (openFile)
        {
            var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Properties.Resources.SelectApkFile,
                AllowMultiple = true,
                FileTypeFilter = new FilePickerFileType[] { new("APK files") { Patterns = new []{ "*.apk" } } }
            });
            if (result.Count == 0) return;
            var paths = from file in result
                let localPath = file.TryGetLocalPath()
                where File.Exists(localPath)
                select localPath;
            await ViewModel!.HandleDroppedItemsAsync(paths);
        }
        else if (openFolder)
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Properties.Resources.SelectGameFolder,
                AllowMultiple = true
            });
            if (result.Count == 0) return;
            var paths = from folder in result
                let localPath = folder.TryGetLocalPath()
                where Directory.Exists(localPath)
                select localPath;
            await ViewModel!.HandleDroppedItemsAsync(paths);
        }
    }
}