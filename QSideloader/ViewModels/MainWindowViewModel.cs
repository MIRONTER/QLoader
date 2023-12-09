using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient.Models;
using AsyncAwaitBestPractices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using QSideloader.Common;
using FluentAvalonia.UI.Controls;
using QSideloader.Converters;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Services;
using QSideloader.Utilities;
using QSideloader.Views;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;
using Serilog.Context;
using JsonSerializerContext = QSideloader.Models.JsonSerializerContext;

namespace QSideloader.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    // ReSharper disable PrivateFieldCanBeConvertedToLocalVariable
    private readonly AdbService _adbService;
    private readonly DownloaderService _downloaderService;
    private readonly IMainWindow _mainWindow;

    private readonly SettingsData _sideloaderSettings;
    // ReSharper restore PrivateFieldCanBeConvertedToLocalVariable

    private readonly Subject<Unit> _gameDonateSubject = new();

    private readonly GameDetailsViewModel _gameDetailsViewModel = new();

    public MainWindowViewModel(IMainWindow mainWindow)
    {
        _adbService = AdbService.Instance;
        _downloaderService = DownloaderService.Instance;
        _mainWindow = mainWindow;
        _sideloaderSettings = Globals.SideloaderSettings;
        ShowGameDetails = ReactiveCommand.Create<Game>(game =>
        {
            if (_downloaderService.AvailableGames is null) return;
            Log.Debug("Opening game details dialog for {GameName}", game.GameName);
            _gameDetailsViewModel.Game = game;
            var dialog = new GameDetailsWindow(_gameDetailsViewModel);
            if (Application.Current is null) return;
            var window = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow!;
            dialog.Show(window);
        });
        ShowGameDetails.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error while opening game details dialog");
            ShowErrorNotification(ex, Resources.ErrorGameDetailsDialog);
        });
        ShowConnectionHelpDialog = ReactiveCommand.CreateFromObservable(ShowConnectionHelpDialogImpl);
        ShowAuthHelpDialog = ReactiveCommand.CreateFromObservable(ShowAuthHelpDialogImpl);
        DonateAllGames = ReactiveCommand.CreateFromObservable(DonateAllGamesImpl);
        ShowSharingDialog = ReactiveCommand.CreateFromObservable(ShowSharingOptionsImpl);
        Task.Run(async () => { DonationsAvailable = await _downloaderService.GetDonationsAvailable(); });
        _adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged);
        _adbService.WhenPackageListChanged.Subscribe(_ =>
        {
            Task.Run(RefreshGameDonationBadge).SafeFireAndForget();
            Task.Run(RunAutoDonation).SafeFireAndForget(ex =>
            {
                Log.Error(ex, "Error running auto donation");
                ShowErrorNotification(ex, Resources.ErrorAutoDonation);
            });
        });
        TryInstallTrailersAddon();
        IsDeviceConnected = _adbService.IsDeviceConnected;
    }

    public INotificationManager? NotificationManager { get; set; }
    [Reactive] public bool IsDeviceConnected { get; private set; }
    [Reactive] public bool IsDeviceUnauthorized { get; private set; }

    public ObservableCollection<TaskViewModel> TaskList { get; } = new();

    [Reactive] public int DonatableAppsCount { get; private set; }
    [Reactive] public bool DonationBarShown { get; private set; }
    [Reactive] public bool DonationsAvailable { get; private set; }

    // Navigation menu width: 245 for Russian locale, 210 for others
    public static int NavigationMenuWidth =>
        Thread.CurrentThread.CurrentUICulture.Name.Contains("ru", StringComparison.OrdinalIgnoreCase)
            ? 245
            : 210;

    public IObservable<Unit> WhenGameDonated => _gameDonateSubject.AsObservable();

    public ReactiveCommand<Game, Unit> ShowGameDetails { get; }
    public ReactiveCommand<Unit, Unit> ShowConnectionHelpDialog { get; }
    public ReactiveCommand<Unit, Unit> ShowAuthHelpDialog { get; }
    private ReactiveCommand<Unit, Unit> DonateAllGames { get; }
    public ReactiveCommand<Unit, Unit> ShowSharingDialog { get; }

    public void AddTask(TaskOptions taskOptions)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Don't allow to add duplicate donation tasks
            if (taskOptions is {Type: TaskType.PullAndUpload, App: not null})
            {
                var runningDonations = Globals.MainWindowViewModel!.GetTaskList()
                    .Where(x => x is {TaskType: TaskType.PullAndUpload, IsFinished: false}).ToList();
                if (runningDonations.Any(x => x.PackageName == taskOptions.App.PackageName))
                {
                    Log.Debug("Donation task for {PackageName} already running", taskOptions.App.PackageName);
                    return;
                }
            }

            var task = new TaskViewModel(taskOptions);
            using (LogContext.PushProperty("TaskId", task.TaskId))
            {
                Log.Information("Adding task {TaskId} {TaskType} {TaskName}", task.TaskId, taskOptions.Type,
                    task.TaskName);
                TaskList.Add(task);
                task.Run();
            }
        }).GetTask().ContinueWith(t =>
        {
            if (!t.IsFaulted) return;
            var exception = t.Exception?.InnerException ?? t.Exception!;
            Log.Error(exception, "Error adding task");
            Globals.ShowErrorNotification(exception, Resources.ErrorAddingTask);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public IEnumerable<TaskViewModel> GetTaskList()
    {
        return TaskList.ToList();
    }

    public void OnTaskFinished(bool isSuccess, TaskId taskId)
    {
        if (!isSuccess || !_sideloaderSettings.EnableTaskAutoDismiss) return;
        Task.Delay(_sideloaderSettings.TaskAutoDismissDelay * 1000).ContinueWith(_ =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var task = TaskList.FirstOrDefault(t => Equals(t.TaskId, taskId));
                if (task is null) return;
                Log.Debug("Auto-dismissing completed task {TaskId} {TaskType} {TaskName}", task.TaskId,
                    task.TaskType, task.TaskName);
                TaskList.Remove(task);
            })
        );
    }

    private void OnDeviceStateChanged(DeviceState state)
    {
        switch (state)
        {
            case DeviceState.Online:
                IsDeviceConnected = true;
                IsDeviceUnauthorized = false;
                break;
            case DeviceState.Offline:
                IsDeviceConnected = false;
                IsDeviceUnauthorized = false;
                break;
            case DeviceState.Unauthorized:
                IsDeviceConnected = false;
                IsDeviceUnauthorized = true;
                break;
        }

        Task.Run(RefreshGameDonationBadge).SafeFireAndForget();
    }

    public static async Task HandleDroppedItemsAsync(IEnumerable<string> fileNames)
    {
        foreach (var fileName in fileNames)
            if (Directory.Exists(fileName))
            {
                if (Directory.EnumerateFiles(fileName, ".backup", SearchOption.TopDirectoryOnly).Any())
                {
                    Log.Debug("Dropped folder {FileName} contains backup", fileName);
                    var backup = new Backup(fileName);
                    backup.Restore();
                    continue;
                }

                if (Directory.EnumerateFiles(fileName, "release.json", SearchOption.TopDirectoryOnly).Any())
                {
                    Log.Debug("Dropped folder {FileName} contains release.json", fileName);
                    try
                    {
                        var game = JsonSerializer.Deserialize(
                            await File.ReadAllTextAsync(Path.Combine(fileName,
                                "release.json")), JsonSerializerContext.Default.Game);
                        game!.InstallFromPath(fileName);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to use release.json in {FileName}. Ignoring", fileName);
                    }
                }

                if (Directory.EnumerateFiles(fileName, "*.apk", SearchOption.TopDirectoryOnly).Any())
                {
                    Log.Debug("Dropped folder {FileName} contains APK", fileName);
                    var dirName = Path.GetFileName(fileName);
                    Game game;
                    // Try to find OBB directory and set package name
                    var dirNames = Directory.EnumerateDirectories(fileName, "*.*", SearchOption.TopDirectoryOnly)
                        .Select(Path.GetFileName);
                    var obbDirName = dirNames.Where(d => d is not null).FirstOrDefault(d =>
                        AdbService.PackageNameRegex().IsMatch(d!));
                    if (obbDirName is not null)
                    {
                        Log.Debug("Found OBB directory {ObbDirName}", obbDirName);
                        game = new Game(dirName, dirName, obbDirName);
                    }
                    else
                    {
                        game = new Game(dirName, dirName);
                    }

                    game.InstallFromPath(fileName);
                }
                else
                {
                    Log.Warning("Dropped directory {FileName} does not contain APK files and is not a backup",
                        fileName);
                }
            }
            else if (File.Exists(fileName) && fileName.EndsWith(".apk"))
            {
                Log.Debug("Dropped file {FileName} is an APK", fileName);
                var name = Path.GetFileName(fileName);
                ApkInfo apkInfo;
                try
                {
                    apkInfo = await GeneralUtils.GetApkInfoAsync(fileName);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting APK info for {FileName}", fileName);
                    Globals.ShowErrorNotification(ex, Resources.ErrorApkInfo);
                    continue;
                }

                var game = new Game(apkInfo.ApplicationLabel, name, apkInfo.PackageName);
                game.InstallFromPath(fileName);
            }
            else
            {
                Log.Warning("Unsupported dropped file {FileName}", fileName);
            }
    }

    public void RefreshGameDonationBadge()
    {
        if (!_adbService.IsDeviceConnected || !DonationsAvailable)
        {
            DonatableAppsCount = 0;
            DonationBarShown = false;
            return;
        }

        var donatableApps =
            _adbService.Device!.InstalledApps.Where(app => !app.IsHiddenFromDonation).ToList();
        DonatableAppsCount = donatableApps.Count;
        ShowDonationBarIfNeeded(donatableApps);
    }

    private void ShowDonationBarIfNeeded(List<InstalledApp> donatableApps)
    {
        if (DonatableAppsCount == 0 || _sideloaderSettings.DisableDonationNotification ||
            _sideloaderSettings.EnableAutoDonation || !DonationsAvailable) return;
        Dictionary<string, int> lastDonatableApps = new();
        if (DateTime.Now - _sideloaderSettings.DonationBarLastShown < TimeSpan.FromDays(7))
        {
            lastDonatableApps = _sideloaderSettings.LastDonatableApps;
        }

        var newDonatableApp = false;
        foreach (var app in donatableApps)
        {
            if (lastDonatableApps.TryGetValue(app.PackageName, out var versionCode))
            {
                if (app.VersionCode > versionCode)
                {
                    newDonatableApp = true;
                }
            }
            else
            {
                newDonatableApp = true;
            }
        }

        if (!newDonatableApp) return;
        _sideloaderSettings.DonationBarLastShown = DateTime.Now;
        _sideloaderSettings.LastDonatableApps = donatableApps.ToDictionary(x => x.PackageName, x => x.VersionCode);
        DonationBarShown = true;
    }

    public async Task OnGameDonatedAsync(string packageName, int versionCode)
    {
        var existingDonatedPackage =
            _sideloaderSettings.DonatedPackages.FirstOrDefault(p => p.packageName == packageName);
        if (existingDonatedPackage != default)
        {
            _sideloaderSettings.DonatedPackages.Remove(existingDonatedPackage);
            existingDonatedPackage.Item2 = versionCode;
            _sideloaderSettings.DonatedPackages.Add(existingDonatedPackage);
        }
        else
        {
            _sideloaderSettings.DonatedPackages.Add((packageName, versionCode));
        }

        if (_adbService.Device is not null)
            await _adbService.Device.RefreshInstalledAppsAsync();
        RefreshGameDonationBadge();
        _gameDonateSubject.OnNext(Unit.Default);
    }

    private IObservable<Unit> ShowSharingOptionsImpl()
    {
        return Observable.Start(() =>
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var dialog = new ContentDialog
                {
                    Title = Resources.SelectSharingMethod,
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text = Resources.SelectSharingMethodSubtext
                        }
                    },
                    CloseButtonText = Resources.CloseButton,
                    PrimaryButtonText = Resources.DonateSelectiveButton,
                    PrimaryButtonCommand = ReactiveCommand.Create(() =>
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            _mainWindow.NavigateToGameDonationView();
                            DonationBarShown = false;
                        });
                    }),
                    SecondaryButtonText = Resources.DonateAllButton,
                    SecondaryButtonCommand = DonateAllGames
                };
                dialog.ShowAsync();
            });
        });
    }

    private IObservable<Unit> DonateAllGamesImpl()
    {
        return Observable.Start(() =>
        {
            DonationBarShown = false;
            if (!_adbService.IsDeviceConnected)
            {
                Log.Warning("MainWindowViewModel.DonateAllGamesImpl: no device connection!");
                return;
            }

            Log.Information("Donating all eligible apps");
            var eligibleApps = _adbService.Device!.InstalledApps.Where(app => !app.IsHiddenFromDonation).ToList();
            if (eligibleApps.Count == 0)
            {
                Log.Information("No apps to donate");
                return;
            }

            foreach (var app in eligibleApps)
            {
                app.PullAndUpload();
                Log.Information("Queued for donation: {Name}", app.Name);
            }
        });
    }

    public void ShowNotification(string title, string message, NotificationType type, TimeSpan? expiration = null)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            NotificationManager?.Show(
                new Avalonia.Controls.Notifications.Notification(title, message, type, expiration));
        });
    }

    public void ShowErrorNotification(Exception e, string? message, NotificationType type = NotificationType.Error,
        TimeSpan? expiration = null)
    {
        expiration ??= TimeSpan.Zero;
        // Remove invalid characters to avoid cutting off when copying to clipboard
        var filteredException = CleanStringRegex().Replace(e.ToString(), "");
        var appVersionString = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "N/A";
        var osName = GeneralUtils.GetOsName();
        var osVersion = Environment.OSVersion.VersionString;
        var environment = $"App Version: {appVersionString}\nOS: {osName} {osVersion}";
        var text = message + "\n\n" + filteredException + "\n\n" + environment;
        message += "\n" + Resources.ClickToSeeDetails;
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            NotificationManager?.Show(new Avalonia.Controls.Notifications.Notification(Resources.Error, message,
                type, expiration, () =>
                {
                    var dialog = new ContentDialog
                    {
                        Title = Resources.Error,
                        Content = new ScrollViewer
                        {
                            Content = new TextBox
                            {
                                Text = text,
                                IsReadOnly = true,
                                AcceptsReturn = true
                            },
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible
                        },
                        CloseButtonText = Resources.CloseButton,
                        PrimaryButtonText = Resources.CopyLinkButton,
                        PrimaryButtonCommand = ReactiveCommand.Create(() => CopyLinkAsync(text)),
                        SecondaryButtonText = Resources.CopyToClipboardButton,
                        SecondaryButtonCommand = ReactiveCommand.Create(async () =>
                        {
                            await ClipboardHelper.SetTextAsync(text);
                            ShowNotification(Resources.CopiedToClipboardHeader, Resources.ExceptionCopiedToClipboard,
                                NotificationType.Success);
                        })
                    };
                    dialog.ShowAsync();
                }));
        });
    }

    private async Task CopyLinkAsync(string text)
    {
        try
        {
            var link = await GeneralUtils.CreatePasteAsync(text);
            await ClipboardHelper.SetTextAsync(link);
            Log.Information("Copied link to clipboard: {Link}", link);
            ShowNotification(Resources.CopiedToClipboardHeader,
                Resources.LinkCopiedToClipboard,
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating paste");
            ShowNotification("Error", Resources.CouldntCreateLink, NotificationType.Error,
                TimeSpan.FromSeconds(5));
        }
    }

    private static IObservable<Unit> ShowAuthHelpDialogImpl()
    {
        return Observable.Start(() =>
        {
            var bitmap = BitmapAssetValueConverter.Instance.Convert("/Assets/adbauth.jpg", typeof(Bitmap), null,
                CultureInfo.InvariantCulture) as Bitmap;
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var image = new Image
                {
                    Source = bitmap,
                    MaxWidth = 500
                };
                var textBox = new TextBlock
                {
                    Text = Resources.AdbAuthorizationDialogText
                };
                var stackPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Spacing = 12
                };
                stackPanel.Children.Add(textBox);
                stackPanel.Children.Add(image);
                var dialog = new ContentDialog
                {
                    Title = Resources.AdbAuthorizationDialogTitle,
                    Content = new ScrollViewer
                    {
                        Content = stackPanel
                    },
                    CloseButtonText = Resources.CloseButton
                };
                dialog.ShowAsync();
            });
        });
    }

    private IObservable<Unit> ShowConnectionHelpDialogImpl()
    {
        return Observable.Start(() =>
        {
            var appName = Program.Name;
            var message = string.Format(Resources.AdbConnectionDialogText, appName);

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var dialog = new ContentDialog
                {
                    Title = Resources.AdbConnectionDialogTitle,
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text = message
                        }
                    },
                    CloseButtonText = Resources.CloseButton,
                    PrimaryButtonText = Resources.ForceDevicesRescanButton,
                    PrimaryButtonCommand = ReactiveCommand.Create(() =>
                    {
                        Log.Information("Force connection check requested");
                        ShowNotification(Resources.Info, Resources.RescanningDevices,
                            NotificationType.Information, TimeSpan.FromSeconds(2));
                        Task.Run(async () => await _adbService.CheckDeviceConnectionAsync());
                    }),
                    SecondaryButtonText = "adb devices",
                    SecondaryButtonCommand = ReactiveCommand.Create(ShowAdbDevicesDialogAsync)
                };
                dialog.ShowAsync();
            });
        });
    }

    private async Task ShowAdbDevicesDialogAsync()
    {
        var text = await AdbService.GetDevicesStringAsync();
        Log.Information("adb devices output: {Output}", text);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var dialog = new ContentDialog
            {
                Title = Resources.AdbDevicesDialogTitle,
                Content = new ScrollViewer
                {
                    Content = new TextBox
                    {
                        Text = text,
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap
                    },
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                },
                CloseButtonText = Resources.CloseButton,
                PrimaryButtonText = Resources.CopyToClipboardButton,
                PrimaryButtonCommand = ReactiveCommand.Create(async () =>
                {
                    await ClipboardHelper.SetTextAsync(text);
                    ShowNotification(Resources.CopiedToClipboardHeader, Resources.AdbDevicesCopiedToClipboard,
                        NotificationType.Success);
                }),
                SecondaryButtonText = Resources.ReloadButton,
                SecondaryButtonCommand = ReactiveCommand.Create(ShowAdbDevicesDialogAsync)
            };
            dialog.ShowAsync();
        });
    }

    private void TryInstallTrailersAddon()
    {
        var trailersAddonPath = "";
        if (File.Exists("TrailersAddon.zip"))
            trailersAddonPath = "TrailersAddon.zip";
        if (File.Exists(Path.Combine("..", "TrailersAddon.zip")))
            trailersAddonPath = Path.Combine("..", "TrailersAddon.zip");

        if (string.IsNullOrEmpty(trailersAddonPath)) return;

        Log.Information("Found trailers addon zip. Starting background install");
        var taskOptions = new TaskOptions {Type = TaskType.InstallTrailersAddon, Path = trailersAddonPath};
        AddTask(taskOptions);
    }

    private async Task RunAutoDonation()
    {
        if (!_sideloaderSettings.EnableAutoDonation || !_adbService.IsDeviceConnected) return;
        await _downloaderService.EnsureMetadataAvailableAsync();
        Log.Debug("Running auto donation");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var runningDonations = GetTaskList().Where(x => x.TaskType is TaskType.PullAndUpload && !x.IsFinished)
                .ToList();
            var toDonate = _adbService.Device!.InstalledApps
                .Where(x => !x.IsHiddenFromDonation &&
                            runningDonations.All(d => d.PackageName != x.PackageName)).ToList();
            if (!toDonate.Any()) return;

            Log.Information("Adding donation tasks");
            foreach (var app in toDonate)
            {
                app.PullAndUpload();
            }
        });
    }

    [GeneratedRegex(@"[^\w\d\s\p{P}]")]
    private static partial Regex CleanStringRegex();
}