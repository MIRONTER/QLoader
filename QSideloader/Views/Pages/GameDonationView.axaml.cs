using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views.Pages;

public class GameDonationView : ReactiveUserControl<InstalledAppsViewModel>
{
    public GameDonationView()
    {
        ViewModel = new InstalledAppsViewModel(true);
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}