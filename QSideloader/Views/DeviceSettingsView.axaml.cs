using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;
using ReactiveUI;

namespace QSideloader.Views;

public partial class DeviceSettingsView : ReactiveUserControl<DeviceSettingsViewModel>
{
    public DeviceSettingsView()
    {
        ViewModel = new DeviceSettingsViewModel();
        DataContext = ViewModel;

        
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}