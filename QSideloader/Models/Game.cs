using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using FileHelpers;

namespace QSideloader.Models;

[DelimitedRecord(";")]
[IgnoreFirst]
[SuppressMessage("ReSharper", "PropertyCanBeMadeInitOnly.Global")]
public class Game : INotifyPropertyChanged
{
    [FieldHidden] private bool _isSelected;

    [FieldTrim(TrimMode.Right)] public string? GameName { get; protected set; }

    public string? ReleaseName { get; protected set; }
    public string? PackageName { get; protected set; }
    public int VersionCode { get; protected set; }

    [FieldConverter(ConverterKind.Date, "yyyy-MM-dd HH:mm UTC")]
    public DateTime LastUpdated { get; protected set; }

    public int GameSize { get; protected set; }

    [FieldHidden]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    [FieldHidden] public bool IsNoteAvailable { get; set; }

    [FieldHidden] public string? Note { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class InstalledGame : Game
{
    public InstalledGame(Game game, int installedVersionCode = -1)
    {
        GameName = game.GameName;
        ReleaseName = game.ReleaseName;
        PackageName = game.PackageName;
        AvailableVersionCode = game.VersionCode;
        VersionCode = -1;
        LastUpdated = game.LastUpdated;
        GameSize = game.GameSize;
        InstalledVersionCode = installedVersionCode;
        IsUpdateAvailable = AvailableVersionCode > InstalledVersionCode;
        UpdateStatus = IsUpdateAvailable
            ? $"Update Available! ({InstalledVersionCode} -> {AvailableVersionCode})"
            : "Up To Date";
    }

    public int InstalledVersionCode { get; set; }
    public int AvailableVersionCode { get; set; }
    public bool IsUpdateAvailable { get; set; }
    public string UpdateStatus { get; set; }
}