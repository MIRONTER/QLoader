using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using QSideloader.Models;
using QSideloader.ViewModels;

namespace QSideloader.Views;

public class TaskView : ReactiveUserControl<TaskViewModel>
{
    // Dummy constructor for XAML
    public TaskView()
    {
        InitializeComponent();
    }

    public TaskView(TaskOptions taskOptions)
    {
        TaskType = taskOptions.Type;
        PackageName = taskOptions.Game?.PackageName ?? taskOptions.App?.PackageName;
        ViewModel = new TaskViewModel(taskOptions);
        DataContext = ViewModel;
        InitializeComponent();
    }

    public TaskId TaskId => ViewModel!.TaskId;
    public string TaskName => ViewModel!.TaskName;
    public string? PackageName { get; }
    public TaskType TaskType { get; }
    public bool IsFinished => ViewModel!.IsFinished;

    public Action Cancel
    {
        get
        {
            if (ViewModel != null) return ViewModel.Cancel;
            return () => { };
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void Run()
    {
        ViewModel!.RunTask.Execute().Subscribe();
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void TaskView_OnPointerEnter(object? sender, PointerEventArgs e)
    {
        var border = this.Get<Border>("Border");
        var taskProgressTextBlock = this.Get<TextBlock>("TaskProgressText");
        var hintText = this.Get<TextBlock>("HintText");
        border.Background = new SolidColorBrush(0x1F1F1F);
        taskProgressTextBlock.IsVisible = false;
        hintText.IsVisible = true;
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void TaskView_OnPointerLeave(object? sender, PointerEventArgs e)
    {
        var border = this.Get<Border>("Border");
        var downloadStatsText = this.Get<TextBlock>("TaskProgressText");
        var hintText = this.Get<TextBlock>("HintText");
        border.Background = new SolidColorBrush(0x2C2C2C);
        downloadStatsText.IsVisible = true;
        hintText.IsVisible = false;
    }
}