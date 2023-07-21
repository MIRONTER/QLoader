using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views.Pages;

public partial class GameDonationView : ReactiveUserControl<InstalledAppsViewModel>
{
    public GameDonationView()
    {
        InitializeComponent();
        ViewModel = new InstalledAppsViewModel(true);
        DataContext = ViewModel;
    }
}