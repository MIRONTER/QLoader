using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FileHelpers;
using QSideloader.Utilities;

namespace QSideloader.Models;

[DelimitedRecord(";")]
[IgnoreFirst]
public partial class Game : INotifyPropertyChanged
{
    [FieldHidden] [JsonIgnore] private bool _isInstalled;
    [FieldHidden] [JsonIgnore] private bool _isSelected;
    [FieldHidden] [JsonIgnore] private string? _thumbnailPath;
    [FieldHidden] [JsonIgnore] private string? _originalPackageName;

    public Game()
    {
    }

    public Game(string gameName, string releaseName, int gameSize, string? note)
    {
        GameName = gameName;
        ReleaseName = releaseName;
        GameSize = gameSize;
        if (note is null) return;
        Note = note;
    }

    public Game(string gameName, string releaseName)
    {
        GameName = gameName;
        ReleaseName = releaseName;
    }

    public Game(string? gameName, string? releaseName, string? packageName)
    {
        GameName = gameName;
        ReleaseName = releaseName;
        PackageName = packageName;
    }

    [FieldTrim(TrimMode.Right)] public string? GameName { get; set; }
    public string? ReleaseName { get; set; }
    public string? PackageName { get; set; }
    public int VersionCode { get; set; }

    [FieldConverter(ConverterKind.Date, "yyyy-MM-dd HH:mm UTC", "en")]
    public DateTime LastUpdated { get; set; }

    public int GameSize { get; set; }

    [FieldHidden]
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    [FieldHidden]
    [JsonIgnore]
    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            _isInstalled = value;
            OnPropertyChanged();
        }
    }

    [FieldHidden]
    [JsonIgnore]
    public string ThumbnailPath
    {
        get
        {
            //_thumbnailPath = Path.Combine("Resources", "NoThumbnailImage.png");
            if (_thumbnailPath is not null) return _thumbnailPath;
            if (OriginalPackageName is null) return Path.Combine(PathHelper.ResourcesPath, "NoThumbnailImage.png");
            var jpgPath = Path.Combine(PathHelper.ThumbnailsPath, $"{OriginalPackageName}.jpg");
            var pngPath = Path.Combine(PathHelper.ThumbnailsPath, $"{OriginalPackageName}.png");
            if (File.Exists(jpgPath))
                _thumbnailPath = jpgPath;
            else if (File.Exists(pngPath))
                _thumbnailPath = pngPath;
            else
                // Try finding a thumbnail using case-insensitive enumeration
                try
                {
                    _thumbnailPath = PathHelper.GetActualCaseForFileName(jpgPath);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    try
                    {
                        _thumbnailPath = PathHelper.GetActualCaseForFileName(pngPath);
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        //Log.Debug("No thumbnail found for {OriginalPackageName}", OriginalPackageName);
                        _thumbnailPath = Path.Combine(PathHelper.ResourcesPath, "NoThumbnailImage.png");
                    }
                }

            return _thumbnailPath;
        }
    }
    
    [FieldHidden]
    [JsonIgnore]
    public string? OriginalPackageName
    {
        // We're not setting this from PackageName setter because that makes using CSV reader more complicated
        get
        {
            if (_originalPackageName is not null) return _originalPackageName;
            if (PackageName is null) return null;
            _originalPackageName = KnownRenamesRegex().Replace(PackageName, "");
            return _originalPackageName;
        }
    }

    [FieldHidden] [JsonIgnore] public string? Note { get; set; }

    [FieldHidden]
    [JsonIgnore]
    public Dictionary<string, int?> Popularity { get; } = new() {{"1D", null}, {"7D", null}, {"30D", null}};

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString()
    {
        return ReleaseName ?? "";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Download()
    {
        Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.DownloadOnly, Game = this});
    }

    public virtual void Install()
    {
        Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.DownloadAndInstall, Game = this});
    }

    public void InstallFromPath(string path)
    {
        Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.InstallOnly, Game = this, Path = path});
    }

    public void ShowDetailsWindow()
    {
        Globals.MainWindowViewModel!.ShowGameDetails.Execute(this).Subscribe(_ => { }, _ => { });
    }

    public void Uninstall()
    {
        Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.Uninstall, Game = this});
    }

    public void BackupAndUninstall(BackupOptions backupOptions)
    {
        Globals.MainWindowViewModel!.AddTask(new TaskOptions
            {Type = TaskType.BackupAndUninstall, Game = this, BackupOptions = backupOptions});
    }

    public void Backup(BackupOptions backupOptions)
    {
        Globals.MainWindowViewModel!.AddTask(new TaskOptions
            {Type = TaskType.Backup, Game = this, BackupOptions = backupOptions});
    }

    public static Game FromTestData()
    {
        return new Game("Test", "Test v1337", 1337, null) {IsInstalled = true};
    }
    
    [GeneratedRegex(@"(^mr\.)|(^mrf\.)|(\.jjb)")]
    private static partial Regex KnownRenamesRegex();
}