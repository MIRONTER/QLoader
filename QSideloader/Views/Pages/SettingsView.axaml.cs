using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace QSideloader.Views.Pages;

public class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}