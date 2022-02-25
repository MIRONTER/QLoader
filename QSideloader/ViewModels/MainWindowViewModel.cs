using System.Collections.ObjectModel;
using QSideloader.Views;
using ReactiveUI.Fody.Helpers;

namespace QSideloader.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    [Reactive] public ObservableCollection<TaskView> TaskList { get; set; } = new();
}