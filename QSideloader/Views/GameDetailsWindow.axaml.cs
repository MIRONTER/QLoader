using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace QSideloader.Views;

public partial class GameDetailsWindow : Window
{
    public GameDetailsWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}