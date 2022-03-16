using System;
using System.ComponentModel;
using System.IO;
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
    [Reactive] [JsonProperty] public bool CheckUpdatesOnLaunch { get; set; }
    [Reactive] public string DownloadsLocationTextBoxText { get; set; } = "";
    [JsonProperty] public string DownloadsLocation { get; set; } = "";
    [Reactive] [JsonProperty] public bool DeleteAfterInstall { get; set; }
    [Reactive] [JsonProperty] public bool EnableDebugConsole { get; set; }
    public bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private ReactiveCommand<Unit, Unit> SaveSettings { get; }
    private ReactiveCommand<Unit, Unit> RestoreDefaults { get; }
    public ReactiveCommand<Unit, Unit> SetDownloadLocation { get; }

    private Timer AutoSaveDelayTimer { get; } = new() {AutoReset = false, Interval = 500};

    public ViewModelActivator Activator { get; }

    private void InitDefaults()
    {
        CheckUpdatesOnLaunch = true;
        DownloadsLocation = Path.Combine(Environment.CurrentDirectory, "downloads");
        DownloadsLocationTextBoxText = DownloadsLocation;
        DeleteAfterInstall = false;
#if DEBUG
        EnableDebugConsole = true;
#else
        EnableDebugConsole = false;
#endif
    }

    private void ValidateSettings()
    {
        if (!Directory.Exists(DownloadsLocation))
        {
            var defaultLocation = Path.Combine(Environment.CurrentDirectory, "downloads");
            if (DownloadsLocation == defaultLocation)
            {
                Directory.CreateDirectory(defaultLocation);
                DownloadsLocationTextBoxText = DownloadsLocation;
                return;
            }

            Log.Debug("Downloads location is invalid, resetting");
            Directory.CreateDirectory(defaultLocation);
            DownloadsLocation = defaultLocation;
            SaveSettings.Execute().Subscribe();
        }

        DownloadsLocationTextBoxText = DownloadsLocation;
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
                    File.Move(tmpPath, PathHelper.SettingsPath);
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
            SaveSettings.Execute().Subscribe();
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