using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.Helpers;
using QSideloader.ViewModels;
using ReactiveUI;

namespace QSideloader.Views;

public partial class SideloaderSettingsView : ReactiveUserControl<SideloaderSettingsViewModel>
{
    public SideloaderSettingsView()
    {
        ViewModel = Globals.SideloaderSettings;
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}