using System.Diagnostics.CodeAnalysis;
using Avalonia;
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
}