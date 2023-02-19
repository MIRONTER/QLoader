using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using FluentAvalonia.UI.Controls;
using QSideloader.Controls;
using QSideloader.Models;
using QSideloader.Utilities;
using QSideloader.ViewModels;
using Serilog;

namespace QSideloader.Views.Pages;

public class AvailableGamesView : ReactiveUserControl<AvailableGamesViewModel>
{
    public AvailableGamesView()
    {
        ViewModel = new AvailableGamesViewModel();
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void AvailableGamesDataGrid_OnDoubleTapped(object? sender, RoutedEventArgs e)
    {
        var dataGrid = (DataGrid?) sender;
        if (dataGrid is null || e.Source is FontIcon) return;
        var selectedGame = (Game?) dataGrid.SelectedItem;
        if (selectedGame is null) return;
        // TODO: let user set action in settings?
        //Globals.MainWindowViewModel!.QueueForInstall(selectedGame);
        Globals.MainWindowViewModel!.ShowGameDetailsCommand.Execute(selectedGame).Subscribe(_ => { }, _ => { });
        e.Handled = true;
    }

    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        //Log.Debug("Key pressed: {Key}, modifiers: {Modifiers}", e.Key, e.KeyModifiers);
        if (e.KeyModifiers == KeyModifiers.None)
            switch (e.Key)
            {
                // If user starts typing, focus the search box
                case >= Key.D0 and <= Key.Z:
                    this.Get<TextBox>("SearchBox").Focus();
                    break;
            }
    }
    
    private void MainWindow_OnKeyUp(object? sender, KeyEventArgs e)
    {
        //Log.Debug("Key released: {Key}, modifiers: {Modifiers}", e.Key, e.KeyModifiers);
        var dataGrid = this.Get<DataGrid>("AvailableGamesDataGrid");
        var selectedGame = (Game?) dataGrid.SelectedItem;
        if (e.KeyModifiers == KeyModifiers.None)
            switch (e.Key)
            {
                // If Enter or arrow down/up is pressed, focus the data grid
                case Key.Down or Key.Up or Key.Enter:
                    var isDataGridFocused = dataGrid.IsFocused;
                    if (!isDataGridFocused)
                    {
                        dataGrid.Focus();
                        dataGrid.SelectedIndex = 0;
                    }
                    e.Handled = true;
                    break;
                // If Escape is pressed clear the search box
                case Key.Escape:
                    this.Get<TextBox>("SearchBox").Text = "";
                    e.Handled = true;
                    break;
                // If Space is pressed, toggle the selected game's selected state
                case Key.Space:
                    if (selectedGame is null) return;
                    selectedGame.IsSelected = !selectedGame.IsSelected;
                    e.Handled = true;
                    break;
                // If Alt is pressed, show game details for the selected game
                case Key.LeftAlt or Key.RightAlt:
                    if (selectedGame is null) return;
                    Globals.MainWindowViewModel!.ShowGameDetailsCommand.Execute(selectedGame).Subscribe(_ => { }, _ => { });
                    e.Handled = true;
                    break;
            }
        else
        {
            // If Ctrl+F is pressed, focus the search box
            if (e is {KeyModifiers: KeyModifiers.Control, Key: Key.F})
            {
                this.Get<TextBox>("SearchBox").Focus();
                e.Handled = true;
            }
        }
    }

    private void Visual_OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Subscribe to main window key down event
        if (Application.Current is null) return;
        var mainWindow = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        if (mainWindow is null) return;
        mainWindow.KeyDown += MainWindow_OnKeyDown;
        mainWindow.KeyUp += MainWindow_OnKeyUp;
    }

    private void Visual_OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Unsubscribe from main window key down event
        if (Application.Current is null) return;
        var mainWindow = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        if (mainWindow is null) return;
        mainWindow.KeyDown -= MainWindow_OnKeyDown;
        mainWindow.KeyUp -= MainWindow_OnKeyUp;
    }

    private void AvailableGamesDataGrid_OnEnterKeyDown(object? sender, RoutedEventArgs e)
    {
        var dataGrid = (CustomDataGrid?) sender;
        var selectedGame = (Game?) dataGrid?.SelectedItem;
        if (selectedGame is null) return;
        Log.Debug("Enter key pressed on game {Game}", selectedGame);
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
        var source = e.Source as IControl;
        if (source?.DataContext is not Game selectedGame) return;
        Globals.MainWindowViewModel!.ShowGameDetailsCommand.Execute(selectedGame).Subscribe(_ => { }, _ => { });
        e.Handled = true;
    }
}