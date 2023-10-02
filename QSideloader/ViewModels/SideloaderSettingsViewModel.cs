using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Platform.Storage;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Services;
using QSideloader.Utilities;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using ReactiveUI.Validation.Extensions;
using Serilog;
using JsonSerializerContext = QSideloader.Models.JsonSerializerContext;
using Timer = System.Timers.Timer;

namespace QSideloader.ViewModels;


[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
[SuppressMessage("ReSharper", "PropertyCanBeMadeInitOnly.Global")]
public class SettingsData : ReactiveObject
{
    private static readonly SemaphoreSlim SettingsFileSemaphoreSlim = new(1, 1);
    [JsonIgnore] private static readonly string DefaultDownloadsLocation = PathHelper.DefaultDownloadsPath;
    [JsonIgnore] private static readonly string DefaultBackupsLocation = PathHelper.DefaultBackupsPath;
    private bool _checkUpdatesAutomatically = true;
    private bool _enableDebugConsole;
    private bool _forceEnglish;


    // All properties and accessors must be public for JSON serialization
    public static byte ConfigVersion => 1;

    [Reactive]
    public bool CheckUpdatesAutomatically
    {
        get => _checkUpdatesAutomatically;
        set
        {
            _checkUpdatesAutomatically = value;
            ShowRelaunchNotification();
        }
    }
    [JsonIgnore] public static string[] ConnectionTypes { get; } = {"USB", "Wireless"};
    [Reactive] public string? PreferredConnectionType { get; set; } = ConnectionTypes[0];
    [Reactive] public string DownloadsLocation { get; set; } = DefaultDownloadsLocation;
    [Reactive] public string BackupsLocation { get; set; } = DefaultBackupsLocation;
    [Reactive] public string LastMediaPullLocation { get; set; } = "";
    [Reactive] public string DownloaderBandwidthLimit { get; set; } = "";

    [Reactive]
    public DownloadsPruningPolicy DownloadsPruningPolicy { get; set; } = DownloadsPruningPolicy.DeleteAfterInstall;

    [Reactive]
    public bool EnableDebugConsole
    {
        get => _enableDebugConsole;
        set
        {
            _enableDebugConsole = value;
            ShowRelaunchNotification();
        }
    }
    [Reactive] public string LastWirelessAdbHost { get; set; } = "";
    [JsonIgnore] public static string[] PopularityRanges { get; } = {"30 days", "7 days", "1 day", "None"};
    [Reactive] public string? PopularityRange { get; set; } = PopularityRanges[0];
    public Guid InstallationId { get; set; } = Guid.NewGuid();
    public ObservableCollection<(string packageName, int versionCode)> DonatedPackages { get; set; } = new();
    public ObservableCollection<string> IgnoredDonationPackages { get; } = new();
    [Reactive] public DateTime DonationBarLastShown { get; set; } = DateTime.MinValue;
    [Reactive] public Dictionary<string, int> LastDonatableApps { get; set; } = new();
    [Reactive] public bool EnableRemoteLogging { get; set; }
    [Reactive] public bool EnableAutoDonation { get; set; }
    [Reactive] public bool DisableDonationNotification { get; set; }

    [Reactive]
    public bool ForceEnglish
    {
        get => _forceEnglish;
        set
        {
            ShowRelaunchNotification();
            _forceEnglish = value;
            
        }
    }
    [Reactive] public bool EnableTaskAutoDismiss { get; set; } = true;
    /// <summary>
    /// Task auto dismiss delay in seconds
    /// </summary>
    [Reactive] public int TaskAutoDismissDelay { get; set; } = 10;
    
    private Timer AutoSaveDelayTimer { get; } = new() {AutoReset = false, Interval = 500, Enabled = true};

    public SettingsData()
    {
        PropertyChanged += AutoSave;
        DonatedPackages.CollectionChanged += AutoSave;
        IgnoredDonationPackages.CollectionChanged += AutoSave;
        AutoSaveDelayTimer.Elapsed += (_, _) =>
        {
            Validate(false);
            Save();
        };
    }

    private static SettingsData FromJson(string json)
    {
        return JsonSerializer.Deserialize(json, JsonSerializerContext.Default.SettingsData)!;
    }

    public static SettingsData FromFileOrDefaults(string filePath)
    {
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                return FromJson(json);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to load settings, resetting to defaults");
                return new SettingsData();
            }
        }

        Log.Information("No settings file, loading defaults");
        return new SettingsData();
    }

    public static SettingsData RestoreDefaults(SettingsData settings)
    {
        return new SettingsData
        {
            InstallationId = settings.InstallationId,
            DonatedPackages = settings.DonatedPackages
        };
    }

    public void Validate(bool save = true)
    {
        var saveNeeded = false;
        if (!Directory.Exists(DownloadsLocation))
        {
            if (DownloadsLocation == DefaultDownloadsLocation)
            {
                Directory.CreateDirectory(DefaultDownloadsLocation);
            }
            else
            {
                Log.Debug("Downloads location is invalid, resetting to default");
                Directory.CreateDirectory(DefaultDownloadsLocation);
                DownloadsLocation = DefaultDownloadsLocation;
                saveNeeded = true;
            }
        }

        if (!Directory.Exists(BackupsLocation))
        {
            if (BackupsLocation == DefaultBackupsLocation)
            {
                Directory.CreateDirectory(DefaultBackupsLocation);
            }
            else
            {
                Log.Debug("Backups location is invalid, resetting to default");
                Directory.CreateDirectory(DefaultBackupsLocation);
                BackupsLocation = DefaultBackupsLocation;
                saveNeeded = true;
            }
        }

        if (!Directory.Exists(LastMediaPullLocation))
            if (LastMediaPullLocation != "")
            {
                Log.Debug("Last media pull location is invalid, resetting to default");
                LastMediaPullLocation = "";
                saveNeeded = true;
            }

        if (!ConnectionTypes.Contains(PreferredConnectionType))
        {
            Log.Debug("Preferred connection type is invalid, resetting to default");
            PreferredConnectionType = ConnectionTypes[0];
            saveNeeded = true;
        }

        if (!PopularityRanges.Contains(PopularityRange))
        {
            Log.Debug("Popularity range is invalid, resetting to default");
            PopularityRange = PopularityRanges[0];
            saveNeeded = true;
        }

        if (save && saveNeeded)
            Save();
    }

    public void Save(bool silent = false)
    {
        if (Design.IsDesignMode) return;
        SettingsFileSemaphoreSlim.Wait();
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                TypeInfoResolver = JsonSerializerContext.Default,
                WriteIndented = true
            });
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using FileStream stream = AtomicFileStream.Open(PathHelper.SettingsPath, FileMode.Create,
                    FileAccess.Write, FileShare.Read, 4096, FileOptions.None);
                stream.Write(Encoding.UTF8.GetBytes(json));
            }
            else
            {
                var tmpPath = PathHelper.SettingsPath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, PathHelper.SettingsPath, true);
                File.Delete(tmpPath);
            }

            if (!silent)
                Log.Debug("Settings saved");
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to save settings");
            Globals.ShowErrorNotification(e, Resources.FailedToSaveSettings);
            throw;
        }
        finally
        {
            SettingsFileSemaphoreSlim.Release();
        }
    }
    
    private static void ShowRelaunchNotification()
    {
        Globals.ShowNotification(Resources.Settings, Resources.ApplicationRestartNeededForSetting,
            NotificationType.Information, TimeSpan.FromSeconds(2));
    }
    
    private void AutoSave(object? sender, EventArgs e)
    {
        AutoSaveDelayTimer.Stop();
        AutoSaveDelayTimer.Start();
    }
}

public partial class SideloaderSettingsViewModel : ViewModelBase
{
    private static readonly SemaphoreSlim MirrorSelectionRefreshSemaphoreSlim = new(1, 1);
    private readonly ObservableAsPropertyHelper<bool> _isSwitchingMirror;

    public SideloaderSettingsViewModel()
    {
        Settings = SettingsData.FromFileOrDefaults(PathHelper.SettingsPath);
        Settings.Validate();
        SyncTextBoxes();
        BrowseDownloadsDirectory = ReactiveCommand.CreateFromTask(BrowseDownloadsDirectoryImpl);
        SetDownloadLocation = ReactiveCommand.CreateFromObservable(SetDownloadLocationImpl, this.IsValid());
        BrowseBackupsDirectory = ReactiveCommand.CreateFromTask(BrowseBackupsDirectoryImpl);
        SetBackupsLocation = ReactiveCommand.CreateFromObservable(SetBackupsLocationImpl, this.IsValid());
        SetDownloaderBandwidthLimit =
            ReactiveCommand.CreateFromObservable(SetDownloaderBandwidthLimitImpl, this.IsValid());
        SetTaskAutoDismissDelay = ReactiveCommand.CreateFromObservable(SetTaskAutoDismissDelayImpl, this.IsValid());
        RestoreDefaults = ReactiveCommand.CreateFromObservable(RestoreDefaultsImpl);
        CheckUpdates = ReactiveCommand.CreateFromObservable(CheckUpdatesImpl);
        CheckUpdates.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error checking for updates");
            Globals.ShowErrorNotification(ex, Resources.ErrorCheckingForUpdates);
        });
        SwitchMirror = ReactiveCommand.CreateFromObservable(SwitchMirrorImpl);
        SwitchMirror.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Failed to switch mirror");
            Globals.ShowErrorNotification(ex, Resources.FailedToSwitchMirror);
        });
        ReloadMirrorList = ReactiveCommand.CreateFromTask(ReloadMirrorListImpl);
        var isSwitchingMirrorCombined =
            SwitchMirror.IsExecuting.CombineLatest(ReloadMirrorList.IsExecuting, (a, b) => a || b);
        isSwitchingMirrorCombined.ToProperty(this, x => x.IsSwitchingMirror, out _isSwitchingMirror, false,
            RxApp.MainThreadScheduler);
        IsTrailersAddonInstalled = Directory.Exists(PathHelper.TrailersPath);
        InstallTrailersAddon = ReactiveCommand.CreateFromObservable(InstallTrailersAddonImpl);
        CopyInstallationId = ReactiveCommand.CreateFromTask(CopyInstallationIdImpl);
        RescanDevices = ReactiveCommand.CreateFromObservable(RescanDevicesImpl);
        RescanDevices.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error rescanning devices");
            Globals.ShowErrorNotification(ex, Resources.ErrorRescanningDevices);
        });
        ReconnectDevice = ReactiveCommand.CreateFromObservable(ReconnectDeviceImpl);
        ReconnectDevice.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error reconnecting device");
            Globals.ShowErrorNotification(ex, Resources.ErrorReconnectingDevice);
        });
        RestartAdbServer = ReactiveCommand.CreateFromObservable(RestartAdbServerImpl);
        RestartAdbServer.ThrownExceptions.Subscribe(ex => { Log.Error(ex, "Failed to restart ADB server"); });
        ResetAdbKeys = ReactiveCommand.CreateFromObservable(ResetAdbKeysImpl);
        ResetAdbKeys.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Failed to reset ADB keys");
            Globals.ShowErrorNotification(ex, Resources.FailedToResetAdbKeys);
        });
        ForceCleanupPackage = ReactiveCommand.CreateFromObservable(ForceCleanupPackageImpl);
        CleanLeftoverApks = ReactiveCommand.CreateFromObservable(CleanLeftoverApksImpl);
        CleanLeftoverApks.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error cleaning up leftover APKs");
            Globals.ShowErrorNotification(ex, Resources.ErrorCleaningUpLeftoverApks);
        });
        FixDateTime = ReactiveCommand.CreateFromObservable(FixDateTimeImpl);
        FixDateTime.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error fixing date and time");
            Globals.ShowErrorNotification(ex, Resources.ErrorFixingDateTime);
        });

        this.ValidationRule(viewModel => viewModel.DownloadsLocationTextBoxText,
            Directory.Exists,
            "Invalid path");
        this.ValidationRule(viewModel => viewModel.BackupsLocationTextBoxText,
            Directory.Exists,
            "Invalid path");
        this.ValidationRule(viewModel => viewModel.DownloaderBandwidthLimitTextBoxText,
            x => string.IsNullOrEmpty(x) ||
                 int.TryParse(BandwidthRegex().Match(x).Groups[1].ToString(), out _),
            "Invalid format. Allowed format: Number in KiB/s, or number with suffix B|K|M|G|T|P (e.g. 1000 or 10M)");
        this.ValidationRule(viewModel => viewModel.TaskAutoDismissDelayTextBoxText,
            x => string.IsNullOrEmpty(x) || int.TryParse(x, out _),
            "Invalid format. Allowed format: Number in seconds (e.g. 10)");
        this.WhenAnyValue(x => x.SelectedMirror).Where(s => s is not null)
            .DistinctUntilChanged()
            .Subscribe(_ =>
            {
                SwitchMirror.Execute().Subscribe();
                RefreshMirrorSelection();
            });
    }

    [Reactive] public SettingsData Settings { get; private set; }

    [Reactive] public string DownloadsLocationTextBoxText { get; set; } = "";

    [Reactive] public string BackupsLocationTextBoxText { get; set; } = "";

    [Reactive] public string DownloaderBandwidthLimitTextBoxText { get; set; } = "";

    public DownloadsPruningPolicy DownloadsPruningPolicy
    {
        get => Settings.DownloadsPruningPolicy;
        set => Settings.DownloadsPruningPolicy = value;
    }

    public List<DownloadsPruningPolicy> AllDownloadsPruningPolicies { get; } =
        Enum.GetValues(typeof(DownloadsPruningPolicy)).Cast<DownloadsPruningPolicy>().ToList();

    public string VersionString { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

/*#if DEBUG
    public bool IsConsoleToggleable { get; }
#else
    public bool IsConsoleToggleable { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif*/
    public bool IsConsoleToggleable => true;
    public bool IsUpdaterAvailable { get; } = Globals.Updater is not null;
    [Reactive] public List<string> MirrorList { get; private set; } = new();
    [Reactive] public string? SelectedMirror { get; set; }
    public bool IsSwitchingMirror => _isSwitchingMirror.Value;


    [Reactive] public bool IsTrailersAddonInstalled { get; private set; }

    [Reactive] public string TaskAutoDismissDelayTextBoxText { get; set; } = "";

    public ReactiveCommand<Unit, Unit> RestoreDefaults { get; }
    public ReactiveCommand<Unit, Unit> BrowseDownloadsDirectory { get; }
    public ReactiveCommand<Unit, Unit> SetDownloadLocation { get; }
    public ReactiveCommand<Unit, Unit> BrowseBackupsDirectory { get; }
    public ReactiveCommand<Unit, Unit> SetBackupsLocation { get; }
    public ReactiveCommand<Unit, Unit> SetDownloaderBandwidthLimit { get; }
    public ReactiveCommand<Unit, Unit> SetTaskAutoDismissDelay { get; }
    public ReactiveCommand<Unit, Unit> CheckUpdates { get; }
    private ReactiveCommand<Unit, Unit> SwitchMirror { get; }
    public ReactiveCommand<Unit, Unit> ReloadMirrorList { get; }
    public ReactiveCommand<Unit, Unit> InstallTrailersAddon { get; }
    public ReactiveCommand<Unit, Unit> CopyInstallationId { get; }
    public ReactiveCommand<Unit, Unit> RescanDevices { get; }
    public ReactiveCommand<Unit, Unit> ReconnectDevice { get; }
    public ReactiveCommand<Unit, Unit> RestartAdbServer { get; }
    public ReactiveCommand<Unit, Unit> ResetAdbKeys { get; }
    public ReactiveCommand<Unit, Unit> ForceCleanupPackage { get; }
    public ReactiveCommand<Unit, Unit> CleanLeftoverApks { get; }
    public ReactiveCommand<Unit, Unit> FixDateTime { get; }
    
    private void SyncTextBoxes()
    {
        DownloadsLocationTextBoxText = Settings.DownloadsLocation;
        BackupsLocationTextBoxText = Settings.BackupsLocation;
        DownloaderBandwidthLimitTextBoxText = Settings.DownloaderBandwidthLimit;
        TaskAutoDismissDelayTextBoxText = Settings.TaskAutoDismissDelay.ToString();
    }

    private IObservable<Unit> RestoreDefaultsImpl()
    {
        return Observable.Start(() =>
        {
            Log.Information("Restoring default settings");
            Settings = SettingsData.RestoreDefaults(Settings);
            Settings.Save();
            SyncTextBoxes();
            Globals.ShowNotification(Resources.Info, Resources.RestoredDefaultSettings, NotificationType.Success,
                TimeSpan.FromSeconds(3));
        });
    }

    private IObservable<Unit> SetDownloadLocationImpl()
    {
        return Observable.Start(() =>
        {
            if (DownloadsLocationTextBoxText.EndsWith(Path.DirectorySeparatorChar))
                DownloadsLocationTextBoxText = DownloadsLocationTextBoxText[..^1];
            if (!Directory.Exists(DownloadsLocationTextBoxText) ||
                DownloadsLocationTextBoxText == Settings.DownloadsLocation) return;
            if (!GeneralUtils.IsDirectoryWritable(DownloadsLocationTextBoxText))
            {
                var location = DownloadsLocationTextBoxText;
                DownloadsLocationTextBoxText = Settings.DownloadsLocation;
                Log.Warning("New downloads location {Location} is not writable, not setting", location);
                Globals.ShowNotification(Resources.Error, Resources.LocationNotWritable,
                    NotificationType.Error, TimeSpan.FromSeconds(5));
                return;
            }

            Settings.DownloadsLocation = DownloadsLocationTextBoxText;
            Log.Debug("Set new downloads location: {Location}",
                DownloadsLocationTextBoxText);
            Globals.ShowNotification(Resources.Info, Resources.DownloadsLocationSet, NotificationType.Success,
                TimeSpan.FromSeconds(2));
        });
    }

    private IObservable<Unit> SetBackupsLocationImpl()
    {
        return Observable.Start(() =>
        {
            if (BackupsLocationTextBoxText.EndsWith(Path.DirectorySeparatorChar))
                BackupsLocationTextBoxText = BackupsLocationTextBoxText[..^1];
            if (!Directory.Exists(BackupsLocationTextBoxText) ||
                BackupsLocationTextBoxText == Settings.BackupsLocation) return;
            if (!GeneralUtils.IsDirectoryWritable(BackupsLocationTextBoxText))
            {
                var location = BackupsLocationTextBoxText;
                BackupsLocationTextBoxText = Settings.BackupsLocation;
                Log.Warning("New backups location {Location} is not writable, not setting", location);
                Globals.ShowNotification(Resources.Error, Resources.LocationNotWritable,
                    NotificationType.Error, TimeSpan.FromSeconds(5));
                return;
            }

            Settings.BackupsLocation = BackupsLocationTextBoxText;
            Log.Debug("Set new backups location: {Location}",
                BackupsLocationTextBoxText);
            Globals.ShowNotification(Resources.Info, Resources.BackupsLocationSet, NotificationType.Success,
                TimeSpan.FromSeconds(2));
        });
    }

    private IObservable<Unit> SetDownloaderBandwidthLimitImpl()
    {
        return Observable.Start(() =>
        {
            if (DownloaderBandwidthLimitTextBoxText == Settings.DownloaderBandwidthLimit ||
                !string.IsNullOrEmpty(DownloaderBandwidthLimitTextBoxText) &&
                 !int.TryParse(
                     BandwidthRegex().Match(DownloaderBandwidthLimitTextBoxText).Groups[1].ToString(),
                     out _)) return;
            Settings.DownloaderBandwidthLimit = DownloaderBandwidthLimitTextBoxText;
            if (!string.IsNullOrEmpty(DownloaderBandwidthLimitTextBoxText))
            {
                Log.Debug("Set new downloader bandwidth limit: {Limit}",
                    DownloaderBandwidthLimitTextBoxText);
                Globals.ShowNotification(Resources.Info, Resources.BandwidthLimitSet,
                    NotificationType.Success, TimeSpan.FromSeconds(2));
            }
            else
            {
                Log.Debug("Removed downloader bandwidth limit");
                Globals.ShowNotification(Resources.Info, Resources.BandwidthLimitRemoved,
                    NotificationType.Success, TimeSpan.FromSeconds(2));
            }
        });
    }

    private IObservable<Unit> SetTaskAutoDismissDelayImpl()
    {
        return Observable.Start(() =>
        {
            Log.Debug("Setting new task auto dismiss delay: {Delay}",
                TaskAutoDismissDelayTextBoxText);
            if (string.IsNullOrEmpty(TaskAutoDismissDelayTextBoxText))
                TaskAutoDismissDelayTextBoxText = "10";
            if (int.TryParse(TaskAutoDismissDelayTextBoxText, out var delay))
            {
                if (delay < 0)
                {
                    Log.Warning("Task auto dismiss delay cannot be negative, setting to 0");
                    delay = 0;
                    TaskAutoDismissDelayTextBoxText = "0";
                }

                Settings.TaskAutoDismissDelay = delay;
                Log.Debug("Set new task auto dismiss delay");
                Globals.ShowNotification(Resources.Info, Resources.TaskAutoDismissDelaySet,
                    NotificationType.Success, TimeSpan.FromSeconds(2));
            }
            else
            {
                Log.Warning("Task auto dismiss delay is not a valid number");
                Globals.ShowNotification(Resources.Error, Resources.InputNotANumber,
                    NotificationType.Error, TimeSpan.FromSeconds(5));
            }
        });
    }

    private IObservable<Unit> CheckUpdatesImpl()
    {
        return Observable.Start(() =>
        {
            throw new NotImplementedException();
            /*if (Globals.Updater is null)
            {
                Log.Error("Requested to check for updates, but updater is not initialized");
                throw new InvalidOperationException("Updater is not initialized");
            }*/

            //Dispatcher.UIThread.InvokeAsync(() => Globals.Updater.CheckForUpdatesAtUserRequest());
        });
    }

    public void RefreshMirrorSelection()
    {
        var downloaderService = DownloaderService.Instance;
        if (MirrorSelectionRefreshSemaphoreSlim.CurrentCount == 0) return;
        MirrorSelectionRefreshSemaphoreSlim.Wait();
        try
        {
            MirrorList = downloaderService.MirrorList.ToList();
            SelectedMirror = downloaderService.MirrorName != "" ? downloaderService.MirrorName : null;
        }
        finally
        {
            MirrorSelectionRefreshSemaphoreSlim.Release();
        }
    }

    private IObservable<Unit> SwitchMirrorImpl()
    {
        return Observable.FromAsync(async () =>
        {
            if (MirrorSelectionRefreshSemaphoreSlim.CurrentCount == 0) return;
            var downloaderService = DownloaderService.Instance;
            if (SelectedMirror == downloaderService.MirrorName || SelectedMirror is null) return;
            await downloaderService.TryManualSwitchMirrorAsync(SelectedMirror);
            RefreshMirrorSelection();
        });
    }

    private async Task ReloadMirrorListImpl()
    {
        if (MirrorSelectionRefreshSemaphoreSlim.CurrentCount == 0) return;
        try
        {
            await DownloaderService.Instance.ReloadMirrorListAsync();
        }
        catch
        {
            Globals.ShowNotification(Resources.Error, Resources.FailedToReloadMirrorList, NotificationType.Error,
                TimeSpan.FromSeconds(10));
        }

        RefreshMirrorSelection();
    }

    private IObservable<Unit> InstallTrailersAddonImpl()
    {
        return Observable.Start(() =>
        {
            if (Directory.Exists(PathHelper.TrailersPath))
            {
                IsTrailersAddonInstalled = true;
                return;
            }

            Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.InstallTrailersAddon});
        });
    }

    private async Task BrowseDownloadsDirectoryImpl()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow!;
            var startLocation = await mainWindow.StorageProvider.TryGetFolderFromPathAsync(Settings.DownloadsLocation);
            var result = await mainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Resources.SelectDownloadsFolder,
                SuggestedStartLocation = startLocation
            });
            if (result.Count > 0)
            {
                var path = result[0].TryGetLocalPath();
                if (!Directory.Exists(path))
                {
                    Log.Error("Selected downloads location does not exist: {Path}", path);
                    return;
                }

                DownloadsLocationTextBoxText = path;
                SetDownloadLocation.Execute().Subscribe();
            }
        }
    }

    private async Task BrowseBackupsDirectoryImpl()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow!;
            var startLocation = await mainWindow.StorageProvider.TryGetFolderFromPathAsync(Settings.BackupsLocation);
            var result = await mainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Resources.SelectBackupsFolder,
                SuggestedStartLocation = startLocation
            });
            if (result.Count > 0)
            {
                var path = result[0].TryGetLocalPath();
                if (!Directory.Exists(path))
                {
                    Log.Error("Selected backups location does not exist: {Path}", path);
                    return;
                }

                BackupsLocationTextBoxText = path;
                SetBackupsLocation.Execute().Subscribe();
            }
        }
    }

    private async Task CopyInstallationIdImpl()
    {
        await ClipboardHelper.SetTextAsync(Settings.InstallationId.ToString());
        Globals.ShowNotification(Resources.Info, Resources.InstallationIdCopied, NotificationType.Success,
            TimeSpan.FromSeconds(2));
    }

    private IObservable<Unit> RescanDevicesImpl()
    {
        return Observable.Start(() =>
        {
            Log.Information("Manual device rescan requested");
            var adbService = AdbService.Instance;
            Globals.ShowNotification(Resources.Info, Resources.RescanningDevices, NotificationType.Information,
                TimeSpan.FromSeconds(2));
            adbService.RefreshDeviceList();
            adbService.CheckDeviceConnection();
        });
    }

    private IObservable<Unit> ReconnectDeviceImpl()
    {
        return Observable.Start(() =>
        {
            Log.Information("Device reconnect requested");
            try
            {
                var adbService = AdbService.Instance;
                if (adbService.CheckDeviceConnection())
                {
                    adbService.ReconnectDevice();
                    Globals.ShowNotification(Resources.Info, Resources.DeviceReconnectCompleted,
                        NotificationType.Information,
                        TimeSpan.FromSeconds(2));
                }
                else
                {
                    Globals.ShowNotification(Resources.Error, Resources.NoDeviceConnection, NotificationType.Error,
                        TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Error reconnecting device");
                Globals.ShowErrorNotification(e, Resources.ErrorReconnectingDevice);
            }
        });
    }

    private IObservable<Unit> RestartAdbServerImpl()
    {
        return Observable.Start(() =>
        {
            Log.Information("Manual ADB server restart requested");
            var adbService = AdbService.Instance;
            Globals.ShowNotification(Resources.Info, Resources.RestartingAdbServer, NotificationType.Information,
                TimeSpan.FromSeconds(2));
            Task.Run(async () =>
            {
                await adbService.RestartAdbServerAsync();
                adbService.CheckDeviceConnection();
            });
        });
    }

    private IObservable<Unit> ResetAdbKeysImpl()
    {
        return Observable.Start(() =>
        {
            Log.Information("ADB keys reset requested");
            AdbService.Instance.ResetAdbKeys();
            Globals.ShowNotification(Resources.Info, Resources.AdbKeysReset, NotificationType.Information,
                TimeSpan.FromSeconds(2));
        });
    }

    private IObservable<Unit> ForceCleanupPackageImpl()
    {
        return Observable.Start(() =>
        {
            Log.Information("Force cleanup of package requested (not implemented)");
            Globals.ShowNotification(Resources.Info, Resources.NotImplemented, NotificationType.Information,
                TimeSpan.FromSeconds(2));
        });
    }

    private IObservable<Unit> CleanLeftoverApksImpl()
    {
        return Observable.Start(() =>
        {
            Log.Information("Cleanup of leftover APKs requested");
            var adbService = AdbService.Instance;
            if (adbService.CheckDeviceConnection())
            {
                adbService.Device!.CleanLeftoverApks();
                Globals.ShowNotification(Resources.Info, Resources.LeftoverApksCleanupCompleted,
                    NotificationType.Information,
                    TimeSpan.FromSeconds(2));
            }
            else
            {
                Globals.ShowNotification(Resources.Error, Resources.NoDeviceConnection, NotificationType.Error,
                    TimeSpan.FromSeconds(2));
            }
        });
    }

    private IObservable<Unit> FixDateTimeImpl()
    {
        return Observable.Start(() =>
        {
            Log.Information("Date and time fix requested");
            var adbService = AdbService.Instance;
            if (adbService.CheckDeviceConnection())
            {
                if (!adbService.Device!.TryFixDateTime())
                    throw new Exception("Failed to set date and time on device");
                Globals.ShowNotification(Resources.Info, Resources.DateTimeSet, NotificationType.Information,
                    TimeSpan.FromSeconds(2));
            }
            else
            {
                Globals.ShowNotification(Resources.Error, Resources.NoDeviceConnection, NotificationType.Error,
                    TimeSpan.FromSeconds(2));
            }
        });
    }

    [GeneratedRegex("^(\\d+)[BKMGTP]{0,1}$")]
    private static partial Regex BandwidthRegex();
}

public enum DownloadsPruningPolicy
{
    DeleteAfterInstall,
    Keep1Version,
    Keep2Versions,
    KeepAll
}