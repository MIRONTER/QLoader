using QSideloader.Models;

namespace QSideloader.ViewModels;

public class GameDetailsViewModel
{
    public Game Game { get; }

    public GameDetailsViewModel()
    {
        Game = new Game("GameName", "ReleaseName", 1337, "NoteText");
    }
    public GameDetailsViewModel(Game game)
    {
        Game = game;
    }
}