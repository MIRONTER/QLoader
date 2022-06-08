using System;
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
    public string TaskName => ViewModel?.TaskName ?? "N/A";
    public string? PackageName { get; }
    public TaskType TaskType { get; }
    public bool IsFinished => ViewModel?.IsFinished ?? false;
    public Action Cancel
    {
        get
        {
            if (ViewModel != null) return ViewModel.Cancel;
            return () => { };
        }
    }

    // Dummy constructor for XAML
    public TaskView()
    {
        InitializeComponent();
    }
    public TaskView(Game game, TaskType taskType)
    {
        TaskType = taskType;
        PackageName = game.PackageName;
        ViewModel = new TaskViewModel(game, taskType);
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void Run()
    {
        ViewModel!.RunTask.Execute().Subscribe();
    }

    private void InputElement_OnPointerEnter(object? sender, PointerEventArgs e)
    {
        var border = this.Get<Border>("Border");
        var downloadStatsText = this.Get<TextBlock>("DownloadStatsText");
        var hintText = this.Get<TextBlock>("HintText");
        border.Background = new SolidColorBrush(0x1F1F1F);
        downloadStatsText.IsVisible = false;
        hintText.IsVisible = true;
    }

    private void InputElement_OnPointerLeave(object? sender, PointerEventArgs e)
    {
        var border = this.Get<Border>("Border");
        var downloadStatsText = this.Get<TextBlock>("DownloadStatsText");
        var hintText = this.Get<TextBlock>("HintText");
        border.Background = new SolidColorBrush(0x2C2C2C);
        downloadStatsText.IsVisible = true;
        hintText.IsVisible = false;
    }
}