using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using FluentAvalonia.UI.Controls;
using QSideloader.Models;
using QSideloader.Utilities;
using QSideloader.ViewModels;

namespace QSideloader.Views.Pages;

public class InstalledGamesView : ReactiveUserControl<InstalledGamesViewModel>
{
    public InstalledGamesView()
    {
        ViewModel = new InstalledGamesViewModel();
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void InstalledGamesDataGrid_OnDoubleTapped(object? sender, RoutedEventArgs e)
    {
        var dataGrid = (DataGrid?) sender;
        if (dataGrid is null || e.Source is FontIcon) return;
        var selectedGame = (InstalledGame?) dataGrid.SelectedItem;
        if (selectedGame is null) return;
        // TODO: let user set action in settings?
        //Globals.MainWindowViewModel!.QueueForInstall(selectedGame);
        Globals.MainWindowViewModel!.ShowGameDetailsCommand.Execute(selectedGame);
        e.Handled = true;
    }

    /*private void SetMultiSelectEnabled(bool state)
    {
        if (state)
        {
            ViewModel!.MultiSelectEnabled = true;
            var updateButton = this.FindControl<Button>("UpdateButton");
            updateButton.Content = "Update Selected";
        }
        else
        {
            ViewModel?.ResetSelections();
            ViewModel!.MultiSelectEnabled = false;
            var updateButton = this.FindControl<Button>("UpdateButton");
            updateButton.Content = "Update All";
        }
    }

    private void MultiSelectButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SetMultiSelectEnabled(!ViewModel!.MultiSelectEnabled);
    }*/
}