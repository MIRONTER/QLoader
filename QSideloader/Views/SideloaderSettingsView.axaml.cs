using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.Utilities;
using QSideloader.ViewModels;

namespace QSideloader.Views;

public class SideloaderSettingsView : ReactiveUserControl<SideloaderSettingsViewModel>
{
    public SideloaderSettingsView()
    {
        ViewModel = Globals.SideloaderSettings;
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void Visual_OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ViewModel!.RefreshMirrorSelection();
    }
}