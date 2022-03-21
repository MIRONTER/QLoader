using System;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Platform;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class GameDetailsViewModel
{
    private readonly AdbService _adbService;
    public Game Game { get; }
    [Reactive] public string? ThumbnailPath { get; } = Path.Combine("Resources", "NoThumbnailImage.png");
    public ReactiveCommand<Unit, Unit> DownloadAndInstall { get; }

    public GameDetailsViewModel()
    {
        _adbService = ServiceContainer.AdbService;
        Game = new Game("GameName", "ReleaseName", 1337, "NoteText");
        DownloadAndInstall = ReactiveCommand.CreateFromObservable(DownloadAndInstallImpl);
    }

    public GameDetailsViewModel(Game game)
    {
        _adbService = ServiceContainer.AdbService;
        Game = game;
        DownloadAndInstall = ReactiveCommand.CreateFromObservable(DownloadAndInstallImpl);
        var assets = AvaloniaLocator.Current.GetService<IAssetLoader>();
        if (assets is null) return;
        var jpgPath = Path.Combine("Resources", "thumbnails", $"{Game.PackageName}.jpg");
        var pngPath = Path.Combine("Resources", "thumbnails", $"{Game.PackageName}.png");
        if (File.Exists(jpgPath))
            ThumbnailPath = jpgPath;
        else if (File.Exists(pngPath))
            ThumbnailPath = pngPath;
    }

    private IObservable<Unit> DownloadAndInstallImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnection())
            {
                Log.Warning("InstallImpl: no device connection!");
                return;
            }

            Globals.MainWindowViewModel!.QueueForInstall(Game);
        });
    }
}