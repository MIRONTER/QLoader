using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;
using ReactiveUI;

namespace QSideloader.Views;

public partial class TaskView : ReactiveUserControl<TaskViewModel>
{
    public TaskView()
    {
        ViewModel = new TaskViewModel();
        DataContext = ViewModel;
        this.WhenActivated(disposables => { });
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}