using System;
using System.Drawing;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.Services;
using QSideloader.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class GameDetailsViewModel
{
    public Game Game { get; }
    [Reactive] public string? ThumbnailPath { get; }
    public ReactiveCommand<Unit, Unit> DownloadAndInstall { get; }

    public GameDetailsViewModel()
    {
        Game = new Game("GameName", "ReleaseName", 1337, "NoteText");
        DownloadAndInstall = ReactiveCommand.CreateFromObservable(DownloadAndInstallImpl);
        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
        var assets = AvaloniaLocator.Current.GetService<IAssetLoader>();
        if (assets is null) return;
        ThumbnailPath = $"avares://{assemblyName}/Assets/NoThumbnailImage.png";
    }
    public GameDetailsViewModel(Game game)
    {
        Game = game;
        DownloadAndInstall = ReactiveCommand.CreateFromObservable(DownloadAndInstallImpl);
        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
        var assets = AvaloniaLocator.Current.GetService<IAssetLoader>();
        if (assets is null) return;
        var jpgPath = $"avares://{assemblyName}/Assets/thumbnails/{Game.PackageName}.jpg";
        var pngpath = $"avares://{assemblyName}/Assets/thumbnails/{Game.PackageName}.png";
        if (assets.Exists(new Uri(jpgPath)))
            ThumbnailPath = jpgPath;
        else if (assets.Exists(new Uri(pngpath)))
            ThumbnailPath = pngpath;
        else
            ThumbnailPath = $"avares://{assemblyName}/Assets/NoThumbnailImage.png";
    }
    
    private IObservable<Unit> DownloadAndInstallImpl()
    {
        return Observable.Start(() =>
        {
            if (!ServiceContainer.ADBService.ValidateDeviceConnection())
            {
                Log.Warning("InstallImpl: no device connection!");
                return;
            }

            Globals.MainWindowViewModel!.QueueForInstall(Game);
        });
    }
}