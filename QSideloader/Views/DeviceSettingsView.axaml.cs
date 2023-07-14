using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views;

public partial class DeviceSettingsView : ReactiveUserControl<DeviceSettingsViewModel>
{
    public DeviceSettingsView()
    {
        InitializeComponent();
        ViewModel = new DeviceSettingsViewModel();
        DataContext = ViewModel;
    }
}