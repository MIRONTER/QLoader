using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views;

public partial class InstalledGamesView : ReactiveUserControl<InstalledGamesViewModel>
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