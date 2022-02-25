using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;
using ReactiveUI;

namespace QSideloader.Views;

public partial class DeviceInfoView : ReactiveUserControl<DeviceInfoViewModel>
{
    public DeviceInfoView()
    {
        ViewModel = new DeviceInfoViewModel();
        DataContext = ViewModel;
        this.WhenActivated(disposables => { });
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}