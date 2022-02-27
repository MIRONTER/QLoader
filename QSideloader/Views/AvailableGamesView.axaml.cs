using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using FluentAvalonia.UI.Controls;
using QSideloader.Models;
using QSideloader.ViewModels;
using ReactiveUI;

namespace QSideloader.Views;

public partial class AvailableGamesView : ReactiveUserControl<AvailableGamesViewModel>
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
        ViewModel!.QueueForInstall(selectedGame);
    }
}