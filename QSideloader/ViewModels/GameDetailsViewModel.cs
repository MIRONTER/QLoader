using System;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using AdvancedSharpAdbClient;
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

public class GameDetailsViewModel : ViewModelBase, IActivatableViewModel, IDisposable
{
    private static LibVLC? _libVlc;
    private readonly AdbService _adbService;

    // Dummy constructor for XAML, do not use
    public GameDetailsViewModel()
    {
        Activator = new ViewModelActivator();
        _adbService = AdbService.Instance;
        Game = new Game("GameName", "ReleaseName", 1337, "NoteText");
        try
        {
            _libVlc ??= new LibVLC();
            MediaPlayer = new MediaPlayer(_libVlc);
        }
        catch
        {
            Log.Warning("Failed to initialize LibVLC");
        }

        DownloadAndInstall = ReactiveCommand.CreateFromObservable(DownloadAndInstallImpl);
        DownloadOnly = ReactiveCommand.CreateFromObservable(DownloadOnlyImpl);
    }

    public GameDetailsViewModel(Game game)
    {
        Activator = new ViewModelActivator();
        _adbService = AdbService.Instance;
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
            Log.Verbose(e, "Failed to initialize LibVLC");
        }

        DownloadAndInstall = ReactiveCommand.CreateFromObservable(DownloadAndInstallImpl);
        DownloadOnly = ReactiveCommand.CreateFromObservable(DownloadOnlyImpl);
        var assets = AvaloniaLocator.Current.GetService<IAssetLoader>();
        if (assets is null) return;
        var jpgPath = Path.Combine(PathHelper.ThumbnailsPath, $"{Game.PackageName}.jpg");
        var pngPath = Path.Combine(PathHelper.ThumbnailsPath, $"{Game.PackageName}.png");
        if (File.Exists(jpgPath))
            ThumbnailPath = jpgPath;
        else if (File.Exists(pngPath))
            ThumbnailPath = pngPath;
        else
            // Try finding a thumbnail using case-insensitive enumeration
            try
            {
                ThumbnailPath = PathHelper.GetActualCaseForFileName(jpgPath);
            }
            catch (FileNotFoundException)
            {
                try
                {
                    ThumbnailPath = PathHelper.GetActualCaseForFileName(pngPath);
                }
                catch (FileNotFoundException)
                {
                    // ignored
                }
            }

        this.WhenActivated(disposables =>
        {
            _adbService.WhenDeviceChanged.Subscribe(OnDeviceChanged).DisposeWith(disposables);
            IsDeviceConnected = _adbService.CheckDeviceConnectionSimple();
            Observable.Timer(TimeSpan.FromSeconds(2)).Subscribe(_ => PlayTrailer());
            Disposable.Create(DisposeMediaPlayer).DisposeWith(disposables);
        });
    }

    public Game Game { get; }
    [Reactive] public string? ThumbnailPath { get; set; } = Path.Combine("Resources", "NoThumbnailImage.png");
    public ReactiveCommand<Unit, Unit> DownloadAndInstall { get; }
    public ReactiveCommand<Unit, Unit> DownloadOnly { get; }
    [Reactive] public MediaPlayer? MediaPlayer { get; set; }
    [Reactive] public bool ShowTrailerPlayer { get; set; }
    [Reactive] public bool IsDeviceConnected { get; set; }
    public ViewModelActivator Activator { get; }

    public void Dispose()
    {
        DisposeMediaPlayer();
    }

    private IObservable<Unit> DownloadAndInstallImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnectionSimple())
            {
                Log.Warning("GameDetailsViewModel.DownloadAndInstallImpl: no device connection!");
                IsDeviceConnected = false;
                return;
            }

            Globals.MainWindowViewModel!.EnqueueTask(Game, TaskType.DownloadAndInstall);
        });
    }

    private IObservable<Unit> DownloadOnlyImpl()
    {
        return Observable.Start(() => { Globals.MainWindowViewModel!.EnqueueTask(Game, TaskType.DownloadOnly); });
    }

    private void OnDeviceChanged(AdbService.AdbDevice device)
    {
        switch (device.State)
        {
            case DeviceState.Online:
                IsDeviceConnected = true;
                break;
            case DeviceState.Offline:
                IsDeviceConnected = false;
                break;
        }
    }

    private void PlayTrailer()
    {
        if (_libVlc is null || MediaPlayer is null || !Directory.Exists(PathHelper.TrailersPath)) return;
        var trailerFilePath = Path.Combine(PathHelper.TrailersPath, $"{Game.PackageName}.mp4");
        // Try finding a trailer using case-insensitive enumeration
        try
        {
            var actualTrailerFilePath = PathHelper.GetActualCaseForFileName(trailerFilePath);
            using var media = new Media(_libVlc, actualTrailerFilePath);
            MediaPlayer?.Play(media);
            ShowTrailerPlayer = true;
        }
        catch (FileNotFoundException)
        {
            Log.Debug("Trailer file {TrailerFileName} not found, disabling player", Path.GetFileName(trailerFilePath));
            DisposeMediaPlayer();
        }
    }

    private void DisposeMediaPlayer()
    {
        if (MediaPlayer is null) return;
        // VideoView causes a crash if we just use MediaPlayer.Dispose() here
        var mediaPlayer = MediaPlayer;
        MediaPlayer = null;
        mediaPlayer.Stop();
        ShowTrailerPlayer = false;
        mediaPlayer.Hwnd = IntPtr.Zero;
        mediaPlayer.XWindow = 0U;
        mediaPlayer.Dispose();
    }
}