using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace QSideloader.Controls;

public class CustomDataGrid: DataGrid, IStyleable
{
    Type IStyleable.StyleKey => typeof(DataGrid);
    
    public event EventHandler<RoutedEventArgs> EnterKeyDown
    {
        add => AddHandler(EnterKeyDownEvent, value);
        remove => RemoveHandler(EnterKeyDownEvent, value);
    }

    private static readonly RoutedEvent<RoutedEventArgs> EnterKeyDownEvent =
        RoutedEvent.Register<DataGrid, RoutedEventArgs>(nameof(EnterKeyDown), RoutingStrategies.Bubble);
    
    // Overriden KeyDown event to allow for custom keybindings
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RaiseEvent(new RoutedEventArgs(EnterKeyDownEvent));
            e.Handled = true;
        }
        else
        {
            base.OnKeyDown(e);
        }
    }
}