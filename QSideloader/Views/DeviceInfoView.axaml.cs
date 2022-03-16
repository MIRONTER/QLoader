using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views;

public partial class DeviceInfoView : ReactiveUserControl<DeviceInfoViewModel>
{
    public DeviceInfoView()
    {
        ViewModel = new DeviceInfoViewModel();
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}