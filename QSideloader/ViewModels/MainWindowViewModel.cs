using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Windows.Input;
using QSideloader.Helpers;
using QSideloader.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace QSideloader.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel()
    {
        ShowDialog = new Interaction<GameDetailsViewModel, GameViewModel>();
        ShowGameDetailsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (Globals.AvailableGames is null || Globals.AvailableGames.Length == 0) return;
            var gameDetails = new GameDetailsViewModel(Globals.AvailableGames[0]);
            var result = await ShowDialog.Handle(gameDetails);
        });
    }
    [Reactive] public ObservableCollection<TaskView> TaskList { get; set; } = new();
    
    public ICommand ShowGameDetailsCommand { get; }
    public Interaction<GameDetailsViewModel, GameViewModel> ShowDialog { get; }
}