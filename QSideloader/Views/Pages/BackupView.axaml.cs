using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views.Pages;

public partial class BackupView : ReactiveUserControl<BackupViewModel>
{
    public BackupView()
    {
        ViewModel = new BackupViewModel();
        DataContext = ViewModel;
        InitializeComponent();
    }
}