using System;
using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AdvancedSharpAdbClient.Models;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using LibVLCSharp.Shared;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Services;
using QSideloader.Utilities;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class GameDetailsViewModel : ViewModelBase, IActivatableViewModel
{
    private static LibVLC? _libVlc;
    private Game? _game;

    public GameDetailsViewModel()
    {
        Activator = new ViewModelActivator();
        var adbService = AdbService.Instance;

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
                Globals.ShowErrorNotification(e, Resources.FailedToInitVideoPlayer, NotificationType.Warning,
                    TimeSpan.FromSeconds(5));
            }

        if (Design.IsDesignMode) return;

        Task.Run(TryLoadStoreInfo);

        this.WhenActivated(disposables =>
        {
            adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged).DisposeWith(disposables);
            Task.Run(async () => IsDeviceConnected = await adbService.CheckDeviceConnectionAsync()).DisposeWith(disposables);
            Observable.Timer(TimeSpan.FromSeconds(2)).Subscribe(_ => PlayTrailer()).DisposeWith(disposables);
            Disposable.Create(StopMediaPlayer).DisposeWith(disposables);
        });
    }

    public Game? Game
    {
        get => _game;
        set
        {
            if (value == _game) return;
            _game = value;
            DisplayName = value?.GameName ?? "GameName";
            Task.Run(TryLoadStoreInfo);
        }
    }

    [Reactive] public MediaPlayer? MediaPlayer { get; set; }
    [Reactive] public bool ShowTrailerPlayer { get; set; }
    [Reactive] public bool IsDeviceConnected { get; set; }
    [Reactive] public string DisplayName { get; set; } = "";
    [Reactive] public string Description { get; set; } = "";
    [Reactive] public string StoreRating { get; set; } = "";
    [Reactive] public bool ShowOculusStoreLink { get; set; }
    [Reactive] public string? OculusStoreUrl { get; set; } = "";
    public ViewModelActivator Activator { get; }

    private void OnDeviceStateChanged(DeviceState state)
    {
        IsDeviceConnected = state switch
        {
            DeviceState.Online => true,
            DeviceState.Offline => false,
            _ => IsDeviceConnected
        };
    }

    private void PlayTrailer()
    {
        if (Game is null || _libVlc is null || MediaPlayer is null ||
            !Directory.Exists(PathHelper.TrailersPath)) return;
        var trailerFilePath = Path.Combine(PathHelper.TrailersPath, $"{Game.OriginalPackageName}.mp4");
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
            StopMediaPlayer();
        }
    }

    private void StopMediaPlayer()
    {
        if (MediaPlayer is null) return;
        MediaPlayer.Stop();
        ShowTrailerPlayer = false;
    }

    private async void TryLoadStoreInfo()
    {
        try
        {
            if (Game is null) return;
            var gameInfo = await ApiClient.GetGameStoreInfoAsync(Game.OriginalPackageName);
            if (gameInfo is null) return;
            if (!string.IsNullOrEmpty(gameInfo.DisplayName))
                DisplayName = gameInfo.DisplayName;
            Description = gameInfo.Description ?? "";
            var ratingAggregate = Math.Round(gameInfo.QualityRatingAggregate, 2);
            StoreRating = $"{ratingAggregate} ({gameInfo.RatingCount})";
            OculusStoreUrl = gameInfo.Url;
            if (OculusStoreUrl is not null && gameInfo.PackageName == "com.CMGames.IntoTheRadius")
                ShowOculusStoreLink = true;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to load store info");
        }
    }
}