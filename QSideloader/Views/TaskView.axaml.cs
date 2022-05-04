using System;
using Avalonia.Markup.Xaml;
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
}