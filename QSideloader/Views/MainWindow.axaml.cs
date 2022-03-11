using System;
using System.Linq;
using System.Reactive.Disposables;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;
using QSideloader.ViewModels;
using ReactiveUI;
using Serilog;

namespace QSideloader.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        var thm = AvaloniaLocator.Current.GetService<FluentAvaloniaTheme>();
        if (thm is not null)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                thm.ForceWin32WindowToTheme(this);
            thm.RequestedTheme = FluentAvaloniaTheme.DarkModeString;
        }

        var navigationView = this.FindControl<NavigationView>("NavigationView");
        navigationView.SelectedItem = navigationView.MenuItems.OfType<NavigationViewItem>().First();
        
        this.WhenActivated(d => d(ViewModel!.ShowDialog.RegisterHandler(DoShowDialogAsync)));
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void NavigationView_OnSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.IsSettingsSelected)
        {
            const string pageName = "QSideloader.Views.SettingsView";
            var pageType = Type.GetType(pageName);
            if (pageType is null) return;
            var contentFrame = this.FindControl<Frame>("ContentFrame");
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
            var contentFrame = this.FindControl<Frame>("ContentFrame");
            contentFrame.BackStack.Clear();
            contentFrame.Navigate(pageType);
            Log.Debug("Navigated to {View}", selectedItemTag);
        }
    }

    /*private void TaskListView_OnItemClick(object? sender, ViewRoutedEventArgs e)
    {
        
        var clickedItem = (Avalonia.Extensions.Controls.ListViewItem) e.ClickItem;
        var taskView = (TaskView) clickedItem.GetLogicalChildren().First();
        if (taskView.ViewModel!.IsFinished)
            viewModel.TaskList.Remove(taskView);
    }*/

    private void TaskListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var viewModel = (MainWindowViewModel) DataContext!;
        var listBox = (ListBox?) sender;
        var selectedTask = (TaskView?) e.AddedItems[0];
        if (listBox is null || selectedTask is null) return;
        listBox.SelectedItem = null;
        switch (selectedTask.ViewModel!.IsFinished)
        {
            case true when viewModel.TaskList.Contains(selectedTask):
                if (selectedTask.ViewModel.Game is not null)
                    Log.Debug("Dismissed finished task {GameName}",
                        selectedTask.ViewModel.Game.GameName);
                viewModel.TaskList.Remove(selectedTask);
                break;
            case false:
                selectedTask.ViewModel.Cancel();
                break;
        }
    }

    private void Window_OnOpened(object? sender, EventArgs e)
    {
        //var viewModel = (MainWindowViewModel) DataContext!;
        //viewModel.TaskList.CollectionChanged += TaskListOnCollectionChanged;
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

    private async Task DoShowDialogAsync(InteractionContext<GameDetailsViewModel, GameViewModel?> interaction)
    {
        var dialog = new GameDetailsWindow
        {
            DataContext = interaction.Input
        };

        var result = await dialog.ShowDialog<GameViewModel?>(this);
        interaction.SetOutput(result);
    }
}