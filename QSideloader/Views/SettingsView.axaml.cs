using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.Helpers;
using QSideloader.ViewModels;
using ReactiveUI;

namespace QSideloader.Views;

public partial class SettingsView : ReactiveUserControl<SettingsViewModel>
{
    public SettingsView()
    {
        ViewModel = Globals.Settings;
        DataContext = ViewModel;
        this.WhenActivated(disposables => { });
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}