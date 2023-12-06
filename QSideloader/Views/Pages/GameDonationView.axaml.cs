using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views.Pages;

// ReSharper disable once UnusedType.Global
public partial class GameDonationView : ReactiveUserControl<InstalledAppsViewModel>
{
    public GameDonationView()
    {
        InitializeComponent();
        ViewModel = new InstalledAppsViewModel(true);
        DataContext = ViewModel;
    }
}