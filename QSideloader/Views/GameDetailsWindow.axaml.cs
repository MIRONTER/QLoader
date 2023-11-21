using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views;

public partial class GameDetailsWindow : ReactiveWindow<GameDetailsViewModel>
{
    public GameDetailsWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
    }

    public GameDetailsWindow(GameDetailsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
#if DEBUG
        this.AttachDevTools();
#endif
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void MainWindow_OnActivated(object? sender, EventArgs e)
    {
        Close();
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void Window_OnOpened(object? sender, EventArgs e)
    {
        if (Application.Current is null) return;
        var mainWindow = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        if (mainWindow is null) return;
        mainWindow.Activated += MainWindow_OnActivated;
        mainWindow.KeyUp += OnKeyUp;
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void Window_OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is GameDetailsViewModel)
        {
            DataContext = null;
        }
        if (Application.Current is null) return;
        var mainWindow = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        if (mainWindow is null) return;
        mainWindow.Activated -= MainWindow_OnActivated;
        mainWindow.KeyUp -= OnKeyUp;
    }
}