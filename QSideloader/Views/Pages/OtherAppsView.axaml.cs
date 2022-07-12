using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views.Pages;

public class OtherAppsView : ReactiveUserControl<InstalledAppsViewModel>
{
    public OtherAppsView()
    {
        ViewModel = new InstalledAppsViewModel(false);
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}