using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views.Pages;

public partial class OtherAppsView : ReactiveUserControl<InstalledAppsViewModel>
{
    public OtherAppsView()
    {
        InitializeComponent();
        ViewModel = new InstalledAppsViewModel(false);
        DataContext = ViewModel;
    }
}