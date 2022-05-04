using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views;

public class DeviceSettingsView : ReactiveUserControl<DeviceSettingsViewModel>
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