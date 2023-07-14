using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views;

public partial class TaskView : ReactiveUserControl<TaskViewModel>
{
    // Dummy constructor for XAML
    public TaskView()
    {
        InitializeComponent();
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void TaskView_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        var border = this.Get<Border>("Border");
        var taskProgressTextBlock = this.Get<TextBlock>("TaskProgressText");
        var hintText = this.Get<TextBlock>("HintText");
        border.Background = new SolidColorBrush(0x1F1F1F);
        taskProgressTextBlock.IsVisible = false;
        hintText.IsVisible = true;
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void TaskView_OnPointerExited(object? sender, PointerEventArgs e)
    {
        var border = this.Get<Border>("Border");
        var downloadStatsText = this.Get<TextBlock>("TaskProgressText");
        var hintText = this.Get<TextBlock>("HintText");
        border.Background = new SolidColorBrush(0x2C2C2C);
        downloadStatsText.IsVisible = true;
        hintText.IsVisible = false;
    }
}