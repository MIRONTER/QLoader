using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace QSideloader.Views;

public partial class GameCard : UserControl
{
    public GameCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}