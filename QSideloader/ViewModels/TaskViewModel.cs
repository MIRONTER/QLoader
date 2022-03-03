using System;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using QSideloader.Models;
using QSideloader.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class TaskViewModel : ViewModelBase, IActivatableViewModel
{
    private Game? _game;


    public TaskViewModel()
    {
        PerformTask = ReactiveCommand.CreateFromTask(PerformTaskImpl);
        Activator = new ViewModelActivator();
    }

    public ReactiveCommand<Unit, Unit> PerformTask { get; }

    public Game? Game
    {
        get => _game;
        set
        {
            _game = value;
            GameName = value?.GameName;
        }
    }

    public bool IsFinished { get; private set; }
    [Reactive] public string? GameName { get; private set; } = "N/A";
    [Reactive] public string Status { get; private set; } = "N/A";
    [Reactive] public string DownloadStats { get; private set; } = "";
    private CancellationTokenSource CancellationTokenSource { get; } = new();
    public ViewModelActivator Activator { get; }

    private void RefreshDownloadStats(DownloadStats stats)
    {
        if (stats.SpeedBytes is null || stats.DownloadedMBytes is null || Game is null)
        {
            DownloadStats = "";
            return;
        }

        var progressPercent = Math.Min(Math.Floor((double) stats.DownloadedMBytes / Game.GameSize * 97), 100);

        DownloadStats = $"{progressPercent}%, {stats.SpeedMBytes}MB/s";
    }

    private async Task PerformTaskImpl()
    {
        string? gamePath;
        if (Game is null)
            throw new InvalidOperationException("Game is not set");
        if (!ServiceContainer.ADBService.ValidateDeviceConnection())
        {
            Log.Warning("PerformTaskImpl: no device connection!");
            OnFinished("Failed: no device connection");
            return;
        }

        try
        {
            gamePath = await PerformDownload();
        }
        catch (Exception e)
        {
            OnFinished(e is OperationCanceledException or TaskCanceledException ? "Cancelled" : "Download failed");
            return;
        }

        try
        {
            await PerformInstall(gamePath ?? throw new InvalidOperationException("gamePath is null"));
        }
        catch (Exception e)
        {
            OnFinished(e is OperationCanceledException or TaskCanceledException ? "Cancelled" : "Install failed");
        }
    }

    private async Task<string> PerformDownload()
    {
        var tookDownloadLock = false;
        var downloadStatsSubscription = Disposable.Empty;
        try
        {
            Status = "Download queued";
            await DownloaderService.TakeDownloadLockAsync(CancellationTokenSource.Token);
            tookDownloadLock = true;
            Status = "Downloading";
            downloadStatsSubscription = ServiceContainer.DownloaderService
                .PollStats(TimeSpan.FromMilliseconds(100), ThreadPoolScheduler.Instance)
                .SubscribeOn(RxApp.TaskpoolScheduler)
                .Subscribe(RefreshDownloadStats);
            var gamePath = await Task.Run(() => ServiceContainer.DownloaderService.DownloadGame(Game!,
                CancellationTokenSource.Token));
            downloadStatsSubscription.Dispose();
            DownloadStats = "";
            DownloaderService.ReleaseDownloadLock();
            // if download only OnFinished("Download complete");
            return gamePath;
        }
        finally
        {
            if (tookDownloadLock)
            {
                DownloaderService.ReleaseDownloadLock();
                downloadStatsSubscription.Dispose();
                DownloadStats = "";
            }
        }
    }

    private async Task PerformInstall(string gamePath)
    {
        Status = "Install queued";
        await ADBService.TakeSideloadLockAsync(CancellationTokenSource.Token);
        Status = "Installing";
        if (!ServiceContainer.ADBService.ValidateDeviceConnection())
        {
            Log.Warning("PerformInstall: no device connection!");
            OnFinished("Install failed");
            return;
        }

        ServiceContainer.ADBService.Device!.SideloadGame(Game!, gamePath)
            .SubscribeOn(RxApp.TaskpoolScheduler)
            .Subscribe(
                x => Status = x,
                _ =>
                {
                    ADBService.ReleaseSideloadLock();
                    OnFinished("Install failed");
                },
                () =>
                {
                    ADBService.ReleaseSideloadLock();
                    OnFinished("Installed");
                });
    }

    private void OnFinished(string status)
    {
        IsFinished = true;
        Status = status;
        Log.Information("Task {TaskName} finished. Result: {Status}",
            Game!.GameName, status);
    }

    public void Cancel()
    {
        if (CancellationTokenSource.IsCancellationRequested) return;
        CancellationTokenSource.Cancel();
        Log.Information("Requested cancellation of task {TaskName}", Game!.GameName);
    }
}