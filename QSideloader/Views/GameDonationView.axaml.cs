using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views;

public class GameDonationView : ReactiveUserControl<GameDonationViewModel>
{
    public GameDonationView()
    {
        ViewModel = new GameDonationViewModel();
        DataContext = ViewModel;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}