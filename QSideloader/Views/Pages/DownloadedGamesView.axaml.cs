using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views.Pages;

public partial class DownloadedGamesView : ReactiveUserControl<DownloadedGamesViewModel>
{
    public DownloadedGamesView()
    {
        InitializeComponent();
        ViewModel = new DownloadedGamesViewModel();
        DataContext = ViewModel;
    }
}