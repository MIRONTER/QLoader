using System.Reactive.Disposables;
using ReactiveUI;

namespace QSideloader.ViewModels;

public class DeviceSettingsViewModel : ViewModelBase, IActivatableViewModel
{
    public DeviceSettingsViewModel()
    {
        Activator = new ViewModelActivator();
        this.WhenActivated(disposables =>
        {
            Disposable
                .Create(() => { })
                .DisposeWith(disposables);
        });
    }
    
    public ViewModelActivator Activator { get; }
}