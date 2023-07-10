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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
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
using FluentAvalonia.UI.Controls;
using Newtonsoft.Json;
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

namespace QSideloader.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    // ReSharper disable PrivateFieldCanBeConvertedToLocalVariable
    private readonly AdbService _adbService;
    private readonly DownloaderService _downloaderService;

    private readonly SideloaderSettingsViewModel _sideloaderSettings;
    // ReSharper restore PrivateFieldCanBeConvertedToLocalVariable

    private readonly Subject<Unit> _gameDonateSubject = new();
    private readonly IManagedNotificationManager _notificationManager;

    public MainWindowViewModel(IManagedNotificationManager notificationManager)
    {
        _adbService = AdbService.Instance;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        _notificationManager = notificationManager;
        ShowGameDetailsCommand = ReactiveCommand.Create<Game>(game =>
        {
            if (_downloaderService.AvailableGames is null) return;
            Log.Debug("Opening game details dialog for {GameName}", game.GameName);
            var gameDetails = new GameDetailsViewModel(game);
            var dialog = new GameDetailsWindow(gameDetails);
            if (Application.Current is null) return;
            var mainWindow = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow;
            dialog.Show(mainWindow);
        });
        ShowGameDetailsCommand.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error while opening game details dialog");
            ShowErrorNotification(ex, Resources.ErrorGameDetailsDialog);
        });
        ShowConnectionHelpDialog = ReactiveCommand.CreateFromObservable(ShowConnectionHelpDialogImpl);
        ShowAuthHelpDialog = ReactiveCommand.CreateFromObservable(ShowAuthHelpDialogImpl);
        DonateAllGames = ReactiveCommand.CreateFromObservable(DonateAllGamesImpl);
        ShowSharingDialog = ReactiveCommand.CreateFromObservable(ShowSharingOptionsImpl);
        _adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged);
        _adbService.WhenPackageListChanged.Subscribe(_ =>
        {
            RefreshGameDonationBadge();
            Task.Run(RunAutoDonation).SafeFireAndForget(ex =>
            {
                Log.Error(ex, "Error running auto donation");
                ShowErrorNotification(ex, Resources.ErrorAutoDonation);
            });
        });
        TryInstallTrailersAddon();
        IsDeviceConnected = _adbService.CheckDeviceConnection();
    }

    [Reactive] public bool IsDeviceConnected { get; private set; }
    [Reactive] public bool IsDeviceUnauthorized { get; private set; }
    
    public ObservableCollection<TaskView> TaskList { get; } = new();

    [Reactive] public int DonatableAppsCount { get; private set; }

    // Navigation menu width: 245 for Russian locale, 210 for others
    public static int NavigationMenuWidth =>
        Thread.CurrentThread.CurrentUICulture.Name.Contains("ru", StringComparison.OrdinalIgnoreCase)
            ? 245
            : 210;

    public IObservable<Unit> WhenGameDonated => _gameDonateSubject.AsObservable();

    public ReactiveCommand<Game, Unit> ShowGameDetailsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowConnectionHelpDialog { get; }
    public ReactiveCommand<Unit, Unit> ShowAuthHelpDialog { get; }
    public ReactiveCommand<Unit, Unit> DonateAllGames { get; }
    public ReactiveCommand<Unit, Unit> ShowSharingDialog { get; }

    public void AddTask(TaskOptions taskOptions)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Don't allow to add duplicate donation tasks
            if (taskOptions is { Type: TaskType.PullAndUpload, App: not null })
            {
                var runningDonations = Globals.MainWindowViewModel!.GetTaskList()
                        .Where(x => x.TaskType == TaskType.PullAndUpload && !x.IsFinished).ToList();
                    if (runningDonations.Any(x => x.PackageName == taskOptions.App.PackageName))
                {
                    Log.Debug("Donation task for {PackageName} already running", taskOptions.App.PackageName);
                    return;
                }
            }

            var taskView = new TaskView(taskOptions);
            using (LogContext.PushProperty("TaskId", taskView.TaskId))
            {
                Log.Information("Adding task {TaskId} {TaskType} {TaskName}", taskView.TaskId, taskOptions.Type,
                    taskView.TaskName);
                TaskList.Add(taskView);
                taskView.Run();
            }
        }).ContinueWith(t =>
        {
            if (!t.IsFaulted) return;
            var exception = t.Exception?.InnerException ?? t.Exception!;
            Log.Error(exception, "Error adding task");
            Globals.ShowErrorNotification(exception, Resources.ErrorAddingTask);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public IEnumerable<TaskView> GetTaskList()
    {
        return TaskList.ToList();
    }

    public void OnTaskFinished(bool isSuccess, TaskId taskId)
    {
        if (!isSuccess || !_sideloaderSettings.EnableTaskAutoDismiss) return;
        if (!int.TryParse(_sideloaderSettings.TaskAutoDismissDelayTextBoxText, out var delaySec)) delaySec = 10;
        Task.Delay(delaySec * 1000).ContinueWith(_ =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var task = TaskList.FirstOrDefault(t => Equals(t.TaskId, taskId));
                if (task is null) return;
                Log.Information("Auto-dismissing completed task {TaskId} {TaskType} {TaskName}", task.TaskId,
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

    public void HandleDroppedFiles(IEnumerable<string> fileNames)
    {
        foreach (var fileName in fileNames)
            if (Directory.Exists(fileName))
            {
                if (Directory.EnumerateFiles(fileName, ".backup", SearchOption.TopDirectoryOnly).Any())
                {
                    Log.Debug("Dropped folder {FileName} contains backup", fileName);
                    var backup = new Backup(fileName);
                    AddTask(new TaskOptions { Type = TaskType.Restore, Backup = backup });
                    continue;
                }

                if (Directory.EnumerateFiles(fileName, "release.json", SearchOption.TopDirectoryOnly).Any())
                {
                    Log.Debug("Dropped folder {FileName} contains release.json", fileName);
                    try
                    {
                        var game = JsonConvert.DeserializeObject<Game>(
                            File.ReadAllText(Path.Combine(fileName, "release.json")));
                        AddTask(new TaskOptions { Type = TaskType.InstallOnly, Game = game, Path = fileName });
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
                        Regex.IsMatch(d!, @"^([A-Za-z]{1}[A-Za-z\d_]*\.)+[A-Za-z][A-Za-z\d_]*$"));
                    if (obbDirName is not null)
                    {
                        Log.Debug("Found OBB directory {ObbDirName}", obbDirName);
                        game = new Game(dirName, dirName, obbDirName);
                    }
                    else
                    {
                        game = new Game(dirName, dirName);
                    }

                    AddTask(new TaskOptions { Game = game, Type = TaskType.InstallOnly, Path = fileName });
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
                    apkInfo = GeneralUtils.GetApkInfoAsync(fileName).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error getting APK info for {FileName}", fileName);
                    Globals.ShowErrorNotification(ex, Resources.ErrorApkInfo);
                    continue;
                }

                var game = new Game(apkInfo.ApplicationLabel, name, apkInfo.PackageName);
                AddTask(new TaskOptions { Game = game, Type = TaskType.InstallOnly, Path = fileName });
            }
            else
            {
                Log.Warning("Unsupported dropped file {FileName}", fileName);
            }
    }

    public void RefreshGameDonationBadge()
    {
        DonatableAppsCount = _adbService.CheckDeviceConnectionSimple()
            ? _adbService.Device!.InstalledApps.Count(app => !app.IsHiddenFromDonation)
            : 0;
    }

    public void OnGameDonated(string packageName, int versionCode)
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

        _adbService.Device?.RefreshInstalledApps();
        RefreshGameDonationBadge();
        _gameDonateSubject.OnNext(Unit.Default);
    }

    private IObservable<Unit> ShowSharingOptionsImpl()
    {
        return Observable.Start(() =>
        {
            var appName = Program.Name;
            var message = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? string.Format(Resources.AdbConnectionDialogTextWin, appName)
                : string.Format(Resources.AdbConnectionDialogText, appName);

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var dialog = new ContentDialog
                {
                    Title = Resources.AdbConnectionDialogTitle,
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text = "We found games that you can donate!"
                        }
                    },
                    CloseButtonText = Resources.CloseButton,
                    PrimaryButtonText = "Donate Selective",
                    PrimaryButtonCommand = ReactiveCommand.Create(() =>
                    {
                        Log.Information("Force connection check requested");
                        ShowNotification(Resources.Info, Resources.RescanningDevices,
                            NotificationType.Information, TimeSpan.FromSeconds(2));
                        Task.Run(() => _adbService.CheckDeviceConnection());
                    }),
                    SecondaryButtonText = "Donate All",
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
            if (!_adbService.CheckDeviceConnectionSimple())
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
                Globals.MainWindowViewModel!.AddTask(new TaskOptions { Type = TaskType.PullAndUpload, App = app });
                Log.Information("Queued for donation: {Name}", app.Name);
            }
        });
    }

    public void ShowNotification(string title, string message, NotificationType type, TimeSpan? expiration = null)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            _notificationManager.Show(
                new Avalonia.Controls.Notifications.Notification(title, message, type, expiration));
        });
    }

    public void ShowErrorNotification(Exception e, string? message, NotificationType type = NotificationType.Error,
        TimeSpan? expiration = null)
    {
        expiration ??= TimeSpan.Zero;
        // Remove invalid characters to avoid cutting off when copying to clipboard
        var filteredException = Regex.Replace(e.ToString(), @"[^\w\d\s\p{P}]", "");
        var appVersionString = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "N/A";
        var osName = GeneralUtils.GetOsName();
        var osVersion = Environment.OSVersion.VersionString;
        var environment = $"App Version: {appVersionString}\nOS: {osName} {osVersion}";
        var text = message + "\n\n" + filteredException + "\n\n" + environment;
        message += "\n" + Resources.ClickToSeeDetails;
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            _notificationManager.Show(new Avalonia.Controls.Notifications.Notification(Resources.Error, message,
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
                        PrimaryButtonCommand = ReactiveCommand.Create(async () => { await CopyLink(text); }),
                        SecondaryButtonText = Resources.CopyToClipboardButton,
                        SecondaryButtonCommand = ReactiveCommand.Create(async () =>
                        {
                            await Application.Current!.Clipboard!.SetTextAsync(text);
                            ShowNotification(Resources.CopiedToClipboardHeader, Resources.ExceptionCopiedToClipboard,
                                NotificationType.Success);
                        })
                    };
                    dialog.ShowAsync();
                }));
        });
    }

    private async Task CopyLink(string text)
    {
        try
        {
            var link = await GeneralUtils.CreatePasteAsync(text);
            await Application.Current!.Clipboard!.SetTextAsync(link);
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
            var message = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? string.Format(Resources.AdbConnectionDialogTextWin, appName)
                : string.Format(Resources.AdbConnectionDialogText, appName);

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
                        Task.Run(() => _adbService.CheckDeviceConnection());
                    }),
                    SecondaryButtonText = "adb devices",
                    SecondaryButtonCommand = ReactiveCommand.Create(async () => { await ShowAdbDevicesDialogAsync(); })
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
                    await Application.Current!.Clipboard!.SetTextAsync(text);
                    ShowNotification(Resources.CopiedToClipboardHeader, Resources.AdbDevicesCopiedToClipboard,
                        NotificationType.Success);
                }),
                SecondaryButtonText = Resources.ReloadButton,
                SecondaryButtonCommand = ReactiveCommand.Create(async () => { await ShowAdbDevicesDialogAsync(); })
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
        var taskOptions = new TaskOptions { Type = TaskType.InstallTrailersAddon, Path = trailersAddonPath };
        AddTask(taskOptions);
    }

    private async Task RunAutoDonation()
    {
        if (!_sideloaderSettings.EnableAutoDonation || !_adbService.CheckDeviceConnection()) return;
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
            foreach (var taskOptions in toDonate.Select(app => new TaskOptions
                         { Type = TaskType.PullAndUpload, App = app })) AddTask(taskOptions);
        });
    }
}