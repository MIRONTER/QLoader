using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace QSideloader.Views;

public class LoadingProgressRingView : UserControl
{
    public LoadingProgressRingView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}