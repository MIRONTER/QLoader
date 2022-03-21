using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
using Newtonsoft.Json;
using QSideloader.Helpers;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using ReactiveUI.Validation.Extensions;
using Serilog;

namespace QSideloader.ViewModels;

[JsonObject(MemberSerialization.OptIn)]
public class SideloaderSettingsViewModel : ViewModelBase, IActivatableViewModel
{
    public SideloaderSettingsViewModel()
    {
        Activator = new ViewModelActivator();
        SaveSettings = ReactiveCommand.CreateFromObservable(SaveSettingsImpl);
        SetDownloadLocation = ReactiveCommand.CreateFromObservable(SetDownloadLocationImpl, this.IsValid());
        RestoreDefaults = ReactiveCommand.CreateFromObservable(RestoreDefaultsImpl);
        InitDefaults();
        LoadSettings();
        ValidateSettings();
        PropertyChanged += AutoSave;
        this.ValidationRule(viewModel => viewModel.DownloadsLocationTextBoxText,
            Directory.Exists,
            "Invalid path");
        AutoSaveDelayTimer.Elapsed += (_, _) => SaveSettings.Execute().Subscribe();
    }

    [JsonProperty] private byte ConfigVersion { get; } = 1;
    [Reactive] [JsonProperty] public bool CheckUpdatesOnLaunch { get; private set; }
    public string[] ConnectionTypes { get; } = {"USB", "Wireless"};
    [Reactive] [JsonProperty] public string? PreferredConnectionType { get; private set; }
    [Reactive] public string DownloadsLocationTextBoxText { get; private set; } = "";
    [JsonProperty] public string DownloadsLocation { get; private set; } = "";
    [Reactive] [JsonProperty] public bool DeleteAfterInstall { get; private set; }
    [Reactive] [JsonProperty] public bool EnableDebugConsole { get; private set; }
    [Reactive] [JsonProperty] public string LastWirelessAdbHost { get; set; } = "";
#if DEBUG
    public bool IsConsoleToggleable { get; }
#else
    public bool IsConsoleToggleable { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif
    private ReactiveCommand<Unit, Unit> SaveSettings { get; }
    private ReactiveCommand<Unit, Unit> RestoreDefaults { get; }
    public ReactiveCommand<Unit, Unit> SetDownloadLocation { get; }

    private Timer AutoSaveDelayTimer { get; } = new() {AutoReset = false, Interval = 500};

    public ViewModelActivator Activator { get; }

    private void InitDefaults()
    {
        CheckUpdatesOnLaunch = true;
        PreferredConnectionType = ConnectionTypes[0];
        DownloadsLocation = Path.Combine(Environment.CurrentDirectory, "downloads");
        DownloadsLocationTextBoxText = DownloadsLocation;
        DeleteAfterInstall = false;
        LastWirelessAdbHost = "";
        EnableDebugConsole = !IsConsoleToggleable;
    }

    private void ValidateSettings()
    {
        var saveNeeded = false;
        if (!Directory.Exists(DownloadsLocation))
        {
            var defaultLocation = Path.Combine(Environment.CurrentDirectory, "downloads");
            if (DownloadsLocation == defaultLocation)
            {
                Directory.CreateDirectory(defaultLocation);
                DownloadsLocationTextBoxText = DownloadsLocation;
            }
            else
            {
                Log.Debug("Downloads location is invalid, resetting");
                Directory.CreateDirectory(defaultLocation);
                DownloadsLocation = defaultLocation;
                saveNeeded = true;
            }
            
        }

        if (!ConnectionTypes.Contains(PreferredConnectionType))
        {
            Log.Debug("Preferred connection type is invalid, resetting");
            PreferredConnectionType = ConnectionTypes[0];
            saveNeeded = true;
        }

        DownloadsLocationTextBoxText = DownloadsLocation;
        if (saveNeeded)
            SaveSettings.Execute().Subscribe();
    }

    private void LoadSettings()
    {
        if (File.Exists(PathHelper.SettingsPath))
        {
            try
            {
                var json = File.ReadAllText(PathHelper.SettingsPath);
                JsonConvert.PopulateObject(json, this);
                Log.Information("Loaded settings");
            }
            catch
            {
                Log.Warning("Failed to load settings, using defaults");
                InitDefaults();
            }
        }
        else
        {
            Log.Information("No settings file, using defaults");
            SaveSettings.Execute().Subscribe();
        }
    }

    private IObservable<Unit> SaveSettingsImpl()
    {
        return Observable.Start(() =>
        {
            try
            {
                var json = JsonConvert.SerializeObject(this);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using FileStream stream = AtomicFileStream.Open(PathHelper.SettingsPath, FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read, 4096, FileOptions.None);
                    stream.Write(Encoding.UTF8.GetBytes(json));
                }
                else
                {
                    var tmpPath = PathHelper.SettingsPath + ".tmp";
                    File.WriteAllText(tmpPath, json);
                    File.Move(tmpPath, PathHelper.SettingsPath, true);
                    File.Delete(tmpPath);
                }

                Log.Debug("Settings saved");
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to save settings");
            }
        });
    }

    private IObservable<Unit> RestoreDefaultsImpl()
    {
        return Observable.Start(() =>
        {
            Log.Information("Restoring default settings");
            InitDefaults();
        });
    }

    private IObservable<Unit> SetDownloadLocationImpl()
    {
        return Observable.Start(() =>
        {
            if (!Directory.Exists(DownloadsLocationTextBoxText) ||
                DownloadsLocationTextBoxText == DownloadsLocation) return;
            DownloadsLocation = DownloadsLocationTextBoxText;
            SaveSettings.Execute().Subscribe();
            Log.Debug("Set new downloads location: {DownloadsLocation}",
                DownloadsLocationTextBoxText);
        });
    }

    private void AutoSave(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "HasErrors" or "DownloadsLocationTextBoxText") return;
        AutoSaveDelayTimer.Stop();
        AutoSaveDelayTimer.Start();
    }
}