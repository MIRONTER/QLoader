using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;
using NetSparkleUpdater.UI.Avalonia;
using QSideloader.Helpers;
using QSideloader.ViewModels;
using Serilog;

namespace QSideloader.Views;

public class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    private readonly SideloaderSettingsViewModel _sideloaderSettings;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
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
        var navigationView = this.Get<NavigationView>("NavigationView");
        navigationView.SelectedItem = navigationView.MenuItems.OfType<NavigationViewItem>().First();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // ReSharper disable once UnusedParameter.Local
    private void NavigationView_OnSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.IsSettingsSelected)
        {
            const string pageName = "QSideloader.Views.SettingsView";
            var pageType = Type.GetType(pageName);
            if (pageType is null) return;
            var contentFrame = this.Get<Frame>("ContentFrame");
            contentFrame.BackStack.Clear();
            contentFrame.Navigate(pageType);
            Log.Debug("Navigated to {View}", "SettingsView");
        }
        else
        {
            var selectedItem = (NavigationViewItem) e.SelectedItem;
            var selectedItemTag = (string) selectedItem.Tag!;
            var pageName = "QSideloader.Views." + selectedItemTag;
            var pageType = Type.GetType(pageName);
            if (pageType is null) return;
            var contentFrame = this.Get<Frame>("ContentFrame");
            contentFrame.BackStack.Clear();
            contentFrame.Navigate(pageType);
            Log.Debug("Navigated to {View}", selectedItemTag);
        }
    }

    private void TaskListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var viewModel = (MainWindowViewModel) DataContext!;
        var listBox = (ListBox?) sender;
        var selectedTask = (TaskView?) e.AddedItems[0];
        if (listBox is null || selectedTask is null) return;
        listBox.SelectedItem = null;
        switch (selectedTask.IsFinished)
        {
            case true when viewModel.TaskList.Contains(selectedTask):
                Log.Debug("Dismissed finished task {GameName}",
                    selectedTask.TaskName);
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
        //var viewModel = (MainWindowViewModel) DataContext!;
        //viewModel.TaskList.CollectionChanged += TaskListOnCollectionChanged;
        if (!Design.IsDesignMode)
            InitializeUpdater();
    }

    private void InitializeUpdater()
    {
        Log.Information("Initializing updater");
        Globals.Updater = new SparkleUpdater(
            "https://raw.githubusercontent.com/skrimix/QLoaderFiles/master/appcast.xml",
            new Ed25519Checker(SecurityMode.Unsafe)
        )
        {
            UIFactory = new UIFactory(Icon),
            RelaunchAfterUpdate = true,
            CustomInstallerArguments = "",
            //LogWriter = new LogWriter(true), // uncomment to enable logging to console
            ShowsUIOnMainThread = true
        };
        if (_sideloaderSettings.CheckUpdatesAutomatically)
            Globals.Updater.StartLoop(true);
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
    private async void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing || ViewModel!.TaskList.Count == 0 || ViewModel.TaskList.All(x => x.IsFinished))
        {
            Log.Information("Closing application");
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
        var dragDropPanel = this.Get<StackPanel>("DragDropPanel");
        dragDropPanel.IsVisible = true;
        e.Handled = true;
    }

    private void DragLeave(object? sender, RoutedEventArgs e)
    {
        var dragDropPanel = this.Get<StackPanel>("DragDropPanel");
        dragDropPanel.IsVisible = false;
        e.Handled = true;
    }

    private void Drop(object? sender, DragEventArgs e)
    {
        Log.Debug("DragDrop.Drop event");
        var dragDropPanel = this.Get<StackPanel>("DragDropPanel");
        if (e.Data.Contains(DataFormats.FileNames))
        {
            var files = e.Data.GetFileNames();
            Log.Debug("Dropped folders/files: {Files}", files);
        }
        else
        {
            Log.Warning("Drop data does not contain file names");
        }

        dragDropPanel.IsVisible = false;
        e.Handled = true;
    }
}