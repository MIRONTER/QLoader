using System.Diagnostics.CodeAnalysis;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views;

// ReSharper disable once UnusedType.Global
public partial class TaskView : ReactiveUserControl<TaskViewModel>
{
    public TaskView()
    {
        InitializeComponent();
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void TaskView_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        Border.Background = new SolidColorBrush(0x1F1F1F);
        TaskProgressText.IsVisible = false;
        HintText.IsVisible = true;
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void TaskView_OnPointerExited(object? sender, PointerEventArgs e)
    {
        Border.Background = new SolidColorBrush(0x2C2C2C);
        TaskProgressText.IsVisible = true;
        HintText.IsVisible = false;
    }
}