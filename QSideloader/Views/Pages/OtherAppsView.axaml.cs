using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views.Pages;

// ReSharper disable once UnusedType.Global
public partial class OtherAppsView : ReactiveUserControl<InstalledAppsViewModel>
{
    public OtherAppsView()
    {
        InitializeComponent();
        ViewModel = new InstalledAppsViewModel(false);
        DataContext = ViewModel;
    }
}