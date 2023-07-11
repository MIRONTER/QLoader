using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;
using NetSparkleUpdater;
using NetSparkleUpdater.AssemblyAccessors;
using NetSparkleUpdater.Configurations;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;
using NetSparkleUpdater.UI.Avalonia;
using QSideloader.Utilities;
using QSideloader.ViewModels;
using ReactiveUI;
using Serilog;

namespace QSideloader.Views;

public class MainWindow : ReactiveWindow<MainWindowViewModel>, IMainWindow
{
    private readonly SideloaderSettingsViewModel _sideloaderSettings;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        Title = Program.Name;
        _sideloaderSettings = Globals.SideloaderSettings;
        var thm = AvaloniaLocator.Current.GetService<FluentAvaloniaTheme>();
        if (thm is not null)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                thm.ForceWin32WindowToTheme(this);
            thm.RequestedTheme = FluentAvaloniaTheme.DarkModeString;
        }

        AddHandler(DragDrop.DragEnterEvent, DragEnter);
        AddHandler(DragDrop.DragLeaveEvent, DragLeave);
        AddHandler(DragDrop.DropEvent, Drop);
        
        // Navigate to the first page
        var navigationView = this.Get<NavigationView>("NavigationView");
        navigationView.SelectedItem = navigationView.MenuItems.OfType<NavigationViewItem>().First();
        
        // Recalculate task list height when windows size changes
        this.GetObservable(ClientSizeProperty).Throttle(TimeSpan.FromMilliseconds(100))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RecalculateTaskListBoxHeight());
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void NavigationView_OnSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.IsSettingsSelected)
        {
            const string pageName = "QSideloader.Views.Pages.SettingsView";
            var pageType = Type.GetType(pageName);
            if (pageType is null) return;
            Log.Debug("Navigating to {View}", "SettingsView");
            var contentFrame = this.Get<Frame>("ContentFrame");
            contentFrame.BackStack.Clear();
            contentFrame.Navigate(pageType);
        }
        else
        {
            var selectedItem = (NavigationViewItem)e.SelectedItem;
            var selectedItemTag = (string)selectedItem.Tag!;
            var pageName = "QSideloader.Views.Pages." + selectedItemTag;
            var pageType = Type.GetType(pageName);
            if (pageType is null) return;
            Log.Debug("Navigating to {View}", selectedItemTag);
            var contentFrame = this.Get<Frame>("ContentFrame");
            contentFrame.BackStack.Clear();
            contentFrame.Navigate(pageType);
        }
    }

    public void NavigateToGameDonationView()
    {
        var navigationView = this.Get<NavigationView>("NavigationView");
        navigationView.SelectedItem = navigationView.MenuItems
            .OfType<NavigationViewItem>()
            .First(x => (string?)x.Tag == "GameDonationView");
    }

    private void RecalculateTaskListBoxHeight()
    {
        var windowHeight = ClientSize.Height;
        var taskListBox = this.Get<ListBox>("TaskListBox");
        taskListBox.MaxHeight = (int)windowHeight / (double)3 / 60 * 60;
        //Log.Debug("Recalculated TaskListBox height to {Height}", taskListBox.MaxHeight);
    }

    private void TaskListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var viewModel = (MainWindowViewModel)DataContext!;
        var listBox = (ListBox?)sender;
        var selectedTask = (TaskView?)e.AddedItems[0];
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
        this.Get<NavigationView>("NavigationView").SettingsItem.Content = Properties.Resources.Settings;

        if (Design.IsDesignMode) return;
        InitializeUpdater();
        RecalculateTaskListBoxHeight();
    }

    private void InitializeUpdater()
    {
        if (Globals.Overrides.ContainsKey("DisableSelfUpdate") && 
            bool.TryParse(Globals.Overrides["DisableSelfUpdate"], out var disableSelfUpdate) && disableSelfUpdate ||
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
            Globals.Updater = new SparkleUpdater(appcastUrl, new Ed25519Checker(SecurityMode.Unsafe))
            {
                Configuration = new JSONConfiguration(new AssemblyReflectionAccessor(null), "updater_config.json"),
                UIFactory = new UIFactory(Icon),
                RelaunchAfterUpdate = true,
                CustomInstallerArguments = "",
                //LogWriter = new LogWriter(true), // uncomment to enable logging to console
                ShowsUIOnMainThread = true,
                RestartExecutablePath = Directory.GetCurrentDirectory(),
                RelaunchAfterUpdateCommandPrefix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "" : "./"
            };
            if (_sideloaderSettings.CheckUpdatesAutomatically)
                Globals.Updater.StartLoop(true);
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
    private async void Window_OnClosing(object? sender, CancelEventArgs e)
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
        e.DragEffects = e.Data.Contains(DataFormats.FileNames) ? DragDropEffects.Copy : DragDropEffects.None;
        var dragDropPanel = this.Get<Grid>("DragDropPanel");
        dragDropPanel.IsVisible = true;
        e.Handled = true;
    }

    private void DragLeave(object? sender, RoutedEventArgs e)
    {
        var dragDropPanel = this.Get<Grid>("DragDropPanel");
        dragDropPanel.IsVisible = false;
        e.Handled = true;
    }

    private async void Drop(object? sender, DragEventArgs e)
    {
        Log.Debug("DragDrop.Drop event");
        var dragDropPanel = this.Get<Grid>("DragDropPanel");
        if (e.Data.Contains(DataFormats.FileNames))
        {
            var fileNames = e.Data.GetFileNames()?.ToList();
            if (fileNames is null)
            {
                Log.Warning("e.Data.GetFileNames() returned null");
                return;
            }

            Log.Debug("Dropped folders/files: {Files}", fileNames);
            var viewModel = ViewModel!;
            await Task.Run(() => viewModel.HandleDroppedFiles(fileNames));
        }
        else
        {
            Log.Warning("Drop data does not contain file names");
        }

        dragDropPanel.IsVisible = false;
        e.Handled = true;
    }
}