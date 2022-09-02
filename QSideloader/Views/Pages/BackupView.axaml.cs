using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views.Pages;

public class BackupView : ReactiveUserControl<BackupViewModel>
{
    public BackupView()
    {
        ViewModel = new BackupViewModel();
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}