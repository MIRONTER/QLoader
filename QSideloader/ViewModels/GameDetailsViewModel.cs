using System;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using Avalonia.Controls.Notifications;
using LibVLCSharp.Shared;
using QSideloader.Models;
using QSideloader.Services;
using QSideloader.Utilities;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class GameDetailsViewModel : ViewModelBase, IActivatableViewModel, IDisposable
{
    private static LibVLC? _libVlc;
    private readonly AdbService _adbService;
    private readonly DownloaderService _downloaderService;

    // Dummy constructor for XAML, do not use
    public GameDetailsViewModel()
    {
        Activator = new ViewModelActivator();
        _adbService = AdbService.Instance;
        _downloaderService = DownloaderService.Instance;
        Game = new Game("GameName", "ReleaseName", 1337, "NoteText");
        DisplayName = "GameName";
        Description = "DescriptionText";
        StoreRating = "8.5 (120)";
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
        _downloaderService = DownloaderService.Instance;
        Game = game;
        DisplayName = game.GameName ?? "GameName";
        DownloadAndInstall = ReactiveCommand.CreateFromObservable(DownloadAndInstallImpl);
        DownloadOnly = ReactiveCommand.CreateFromObservable(DownloadOnlyImpl);
        
        // Initialize LibVLC only if trailers are installed
        if (Directory.Exists(PathHelper.TrailersPath))
            try
            {
                // Repeat videos maximum allowed number of times. No loop functionality in libVLC 3
                _libVlc ??= new LibVLC("--input-repeat=65535");
                MediaPlayer = new MediaPlayer(_libVlc);
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to initialize LibVLC");
                Globals.ShowErrorNotification(e, "Failed to initialize video player", NotificationType.Warning,
                    TimeSpan.FromSeconds(5));
            }
        
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
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                try
                {
                    ThumbnailPath = PathHelper.GetActualCaseForFileName(pngPath);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    Log.Warning("No thumbnail found for {PackageName}", Game.PackageName);
                }
            }
        Task.Run(TryLoadStoreInfo);

        this.WhenActivated(disposables =>
        {
            _adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged).DisposeWith(disposables);
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
    [Reactive] public string DisplayName { get; set; }
    [Reactive] public string Description { get; set; } = "";
    [Reactive] public string StoreRating { get; set; } = "";
    [Reactive] public bool ShowOculusStoreLink { get; set; }
    [Reactive] public string? OculusStoreUrl { get; set; } = "";
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

            Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.DownloadAndInstall, Game = Game});
        });
    }

    private IObservable<Unit> DownloadOnlyImpl()
    {
        return Observable.Start(() => { Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.DownloadOnly, Game = Game}); });
    }

    private void OnDeviceStateChanged(DeviceState state)
    {
        switch (state)
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

    private async void TryLoadStoreInfo()
    {
        try
        {
            var game = await _downloaderService.GetGameStoreInfo(Game.PackageName);
            if (game is null) return;
            if (!string.IsNullOrEmpty(game.DisplayName))
                DisplayName = game.DisplayName;
            Description = game.Description ?? "";
            var ratingAggregate = Math.Round(game.QualityRatingAggregate, 2);
            StoreRating = $"{ratingAggregate} ({game.RatingCount})";
            OculusStoreUrl = game.Url;
            if (OculusStoreUrl is not null && game.PackageName == "com.CMGames.IntoTheRadius")
                ShowOculusStoreLink = true;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to load store info");
        }
    }
}