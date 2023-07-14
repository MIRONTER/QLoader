using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views;

public partial class DeviceInfoView : ReactiveUserControl<DeviceInfoViewModel>
{
    public DeviceInfoView()
    {
        InitializeComponent();
        ViewModel = new DeviceInfoViewModel();
        DataContext = ViewModel;
    }
}