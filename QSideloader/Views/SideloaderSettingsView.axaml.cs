using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using QSideloader.Utilities;
using QSideloader.ViewModels;

namespace QSideloader.Views;

public partial class SideloaderSettingsView : ReactiveUserControl<SideloaderSettingsViewModel>
{
    public SideloaderSettingsView()
    {
        InitializeComponent();
        ViewModel = Globals.SideloaderSettingsViewModel;
        DataContext = ViewModel;
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void Visual_OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ViewModel!.RefreshMirrorSelection();
    }

    private void VersionString_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if(e.Source is InputElement inputElement)
        {
            inputElement.Cursor = new Cursor(StandardCursorType.Hand);
        }
    }

    private void VersionString_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if(e.Source is InputElement inputElement)
        {
            inputElement.Cursor = new Cursor(StandardCursorType.Arrow);
        }
    }
}