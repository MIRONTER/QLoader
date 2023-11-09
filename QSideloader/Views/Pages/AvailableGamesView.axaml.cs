using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using FluentAvalonia.UI.Controls;
using QSideloader.Controls;
using QSideloader.Models;
using QSideloader.Utilities;
using QSideloader.ViewModels;

namespace QSideloader.Views.Pages;

public partial class AvailableGamesView : ReactiveUserControl<AvailableGamesViewModel>
{
    public AvailableGamesView()
    {
        InitializeComponent();
        ViewModel = new AvailableGamesViewModel();
        DataContext = ViewModel;
    }

    [SuppressMessage("ReSharper", "SuggestBaseTypeForParameter")]
    private void AvailableGamesDataGrid_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        var dataGrid = (DataGrid?) sender;
        if (dataGrid is null || e.Source is FontIcon) return;
        var selectedGame = (Game?) dataGrid.SelectedItem;
        if (selectedGame is null) return;
        // TODO: let user set action in settings?
        //Globals.MainWindowViewModel!.QueueForInstall(selectedGame);
        selectedGame.ShowDetailsWindow();
        e.Handled = true;
    }

    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        //Log.Debug("Key pressed: {Key}, modifiers: {Modifiers}", e.Key, e.KeyModifiers);
        var dataGrid = AvailableGamesDataGrid;
        var selectedGame = (Game?) dataGrid.SelectedItem;
        if (e.KeyModifiers == KeyModifiers.None)
        {
            switch (e.Key)
            {
                // Enter or arrow down/up - focus the data grid
                case Key.Down or Key.Up or Key.Enter:
                    if (!dataGrid.IsFocused)
                    {
                        dataGrid.Focus();
                        dataGrid.SelectedIndex = 0;
                    }
                    break;
                // Escape - clear the search box
                case Key.Escape:
                    SearchBox.Text = "";
                    break;
                // Space - toggle the highlighted game's selected state
                case Key.Space:
                    if (selectedGame is null) return;
                    selectedGame.IsSelected = !selectedGame.IsSelected;
                    break;
                // F5 - refresh the list
                case Key.F5:
                    ViewModel!.Refresh.Execute(true).Subscribe(_ => { }, _ => { });
                    break;
                // If user starts typing, focus the search box
                case >= Key.D0 and <= Key.Z:
                    if (!SearchBox.IsFocused)
                    {
                        SearchBox.Text = "";
                        SearchBox.Focus();
                    }

                    break;
            }
        }
        else
        {
            // LeftAlt or RightAlt - show game details for the highlighted game
            if (e is {KeyModifiers: KeyModifiers.Alt, Key: Key.LeftAlt or Key.RightAlt})
            {
                if (selectedGame is null) return;
                Globals.MainWindowViewModel!.ShowGameDetails.Execute(selectedGame)
                    .Subscribe(_ => { }, _ => { });
                e.Handled = true;
            }
            // Ctrl+F - focus the search box
            if (e is {KeyModifiers: KeyModifiers.Control, Key: Key.F})
            {
                SearchBox.Focus();
                // Highlight all text
                SearchBox.SelectionStart = 0;
                SearchBox.SelectionEnd = SearchBox.Text?.Length ?? 0;
                e.Handled = true;
            }
        }
    }

    // ReSharper disable UnusedParameter.Local
    private void Visual_OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Subscribe to main window key down event
        if (Application.Current is null) return;
        var mainWindow = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        if (mainWindow is null) return;
        mainWindow.KeyDown += MainWindow_OnKeyDown;
    }

    private void Visual_OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Unsubscribe from main window key down event
        if (Application.Current is null) return;
        var mainWindow = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        if (mainWindow is null) return;
        mainWindow.KeyDown -= MainWindow_OnKeyDown;
    }
    // ReSharper restore UnusedParameter.Local

    private void AvailableGamesDataGrid_OnEnterKeyDown(object? sender, RoutedEventArgs e)
    {
        var dataGrid = (CustomDataGrid?) sender;
        var selectedGame = (Game?) dataGrid?.SelectedItem;
        if (selectedGame is null) return;
        //Log.Debug("Enter key pressed on game {Game}", selectedGame);
        var viewModel = (AvailableGamesViewModel?) DataContext;
        if (viewModel is null) return;
        if (viewModel.IsDeviceConnected)
            viewModel.InstallSingle.Execute(selectedGame).Subscribe(_ => { }, _ => { });
        else
            viewModel.DownloadSingle.Execute(selectedGame).Subscribe(_ => { }, _ => { });
        e.Handled = true;
    }

    private void AvailableGamesDataGrid_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var dataGrid = (DataGrid?) sender;
        if (dataGrid is null || e.InitialPressMouseButton != MouseButton.Middle) return;
        var source = e.Source as Control;
        if (source?.DataContext is not Game selectedGame) return;
        selectedGame.ShowDetailsWindow();
        e.Handled = true;
    }
}