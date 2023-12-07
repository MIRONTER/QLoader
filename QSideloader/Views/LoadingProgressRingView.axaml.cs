using System.Linq;
using Avalonia;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace QSideloader.Views;

public partial class LoadingProgressRingView : UserControl
{
    public LoadingProgressRingView()
    {
        InitializeComponent();
        IsVisibleProperty.Changed.AddClassHandler<LoadingProgressRingView>(OnIsVisibleChanged);
    }
    
    private void OnIsVisibleChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // HACK: This is needed because the progress ring refuses to disable sometimes and causes constant rendering
        if (e.NewValue is not bool isVisible)
            return;
        if (isVisible)
        {
            var progressRing = new ProgressRing
            {
                Height = 55,
                Margin = new Thickness(0, 20, 0, 0),
                IsEnabled = true,
                IsIndeterminate = true,
                Name = "LoadingProgressRing"
            };
            StackPanel.Children.Insert(0, progressRing);
        }
        else
        {
            var progressRing = StackPanel.Children.FirstOrDefault(x => x.Name == "LoadingProgressRing");
            if (progressRing != null) StackPanel.Children.Remove(progressRing);
        }
    }
}