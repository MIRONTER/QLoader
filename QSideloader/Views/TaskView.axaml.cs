using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views;

public partial class TaskView : ReactiveUserControl<TaskViewModel>
{
    public TaskView()
    {
        ViewModel = new TaskViewModel();
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}