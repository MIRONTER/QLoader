using Avalonia;
using Avalonia.Controls;

namespace QSideloader.Controls;

public class CustomBorder : Border
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<CustomBorder, bool>(nameof(IsOpen));

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    static CustomBorder()
    {
        AffectsRender<CustomBorder>(IsOpenProperty);
    }
}