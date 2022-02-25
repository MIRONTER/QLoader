using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace QSideloader.Controls;

public class CustomBorder : Border, IStyleable
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<Border, bool>(nameof(IsOpen));

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    Type IStyleable.StyleKey => typeof(Border);
}