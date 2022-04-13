using System;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Platform;
using LibVLCSharp.Shared;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class GameDetailsViewModel: ViewModelBase, IActivatableViewModel
{
    private readonly AdbService _adbService;
    private static LibVLC? _libVlc;
    public Game Game { get; }
    [Reactive] public string? ThumbnailPath { get; set; } = Path.Combine("Resources", "NoThumbnailImage.png");
    public ReactiveCommand<Unit, Unit> DownloadAndInstall { get; }
    [Reactive] public MediaPlayer? MediaPlayer { get; set; }
    [Reactive] public bool IsTrailerPlaying { get; set; }
    public ViewModelActivator Activator { get; }

    public GameDetailsViewModel()
    {
        Activator = new ViewModelActivator();
        _adbService = ServiceContainer.AdbService;
        Game = new Game("GameName", "ReleaseName", 1337, "NoteText");
        try
        {
            _libVlc ??= new LibVLC();
            MediaPlayer = new MediaPlayer(_libVlc);
        }
        catch
        {
            Log.Warning("Failed to initialize LibVLC player");
        }
        DownloadAndInstall = ReactiveCommand.CreateFromObservable(DownloadAndInstallImpl);
    }

    public GameDetailsViewModel(Game game)
    {
        Activator = new ViewModelActivator();
        _adbService = ServiceContainer.AdbService;
        Game = game;
        try
        {
            // Repeat videos maximum allowed number of times. No loop functionality in libVLC 3
            _libVlc ??= new LibVLC("--input-repeat=65535");
            MediaPlayer = new MediaPlayer(_libVlc);
        }
        catch (Exception e)
        {
            Log.Warning("Failed to initialize LibVLC");
            Log.Verbose(e, "");
        }
        DownloadAndInstall = ReactiveCommand.CreateFromObservable(DownloadAndInstallImpl);
        var assets = AvaloniaLocator.Current.GetService<IAssetLoader>();
        if (assets is null) return;
        var jpgPath = Path.Combine(PathHelper.ThumbnailsPath, $"{Game.PackageName}.jpg");
        var pngPath = Path.Combine(PathHelper.ThumbnailsPath, $"{Game.PackageName}.png");
        if (File.Exists(jpgPath))
            ThumbnailPath = jpgPath;
        else if (File.Exists(pngPath))
            ThumbnailPath = pngPath;
        this.WhenActivated(disposables =>
        { 
           Observable.Timer(TimeSpan.FromSeconds(2)).Subscribe(_ => PlayTrailer());
           Disposable.Create(DisposeMediaPlayer).DisposeWith(disposables);
        });
    }

    private IObservable<Unit> DownloadAndInstallImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnection())
            {
                Log.Warning("GameDetailsViewModel.InstallImpl: no device connection!");
                return;
            }

            Globals.MainWindowViewModel!.QueueForInstall(Game);
        });
    }

    private void PlayTrailer()
    {
        if (_libVlc is null || MediaPlayer is null) return;
        var trailerFilePath = Path.Combine(PathHelper.TrailersPath, $"{Game.PackageName}.mp4");
        if (File.Exists(trailerFilePath))
        {
            using var media = new Media(_libVlc, trailerFilePath);
            MediaPlayer?.Play(media);
            IsTrailerPlaying = true;
            return;
        }

        Log.Debug("Trailer file {TrailerFileName} not found, disabling player", Path.GetFileName(trailerFilePath));
        DisposeMediaPlayer();
    }

    private void DisposeMediaPlayer()
    {
        if (MediaPlayer is null) return;
        // VideoView causes a crash if we just use MediaPlayer.Dispose() here
        var mediaPlayer = MediaPlayer;
        MediaPlayer = null;
        mediaPlayer.Stop();
        IsTrailerPlaying = false;
        mediaPlayer.Hwnd = IntPtr.Zero;
        mediaPlayer.XWindow = 0U;
        mediaPlayer.Dispose();
    }
}