using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using FileHelpers;
using Newtonsoft.Json;

// ReSharper disable MemberCanBePrivate.Global

namespace QSideloader.Models;

[DelimitedRecord(";")]
[IgnoreFirst]
[SuppressMessage("ReSharper", "PropertyCanBeMadeInitOnly.Global")]
public class Game : INotifyPropertyChanged
{
    [FieldHidden] [JsonIgnoreAttribute] private bool _isSelected;

    [FieldTrim(TrimMode.Right)] public string? GameName { get; protected set; }

    public string? ReleaseName { get; protected set; }
    public string? PackageName { get; protected set; }
    public int VersionCode { get; protected set; }

    [FieldConverter(ConverterKind.Date, "yyyy-MM-dd HH:mm UTC")]
    public DateTime LastUpdated { get; protected set; }

    public int GameSize { get; protected set; }

    [FieldHidden] [JsonIgnoreAttribute]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    [FieldHidden] [JsonIgnoreAttribute] public bool IsNoteAvailable { get; set; }

    [FieldHidden] [JsonIgnoreAttribute] public string? Note { get; set; }

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
        VersionCode = game.VersionCode;
        LastUpdated = game.LastUpdated;
        GameSize = game.GameSize;
        InstalledVersionCode = installedVersionCode;
        IsUpdateAvailable = AvailableVersionCode > InstalledVersionCode;
        UpdateStatus = IsUpdateAvailable
            ? $"Update Available! ({InstalledVersionCode} -> {AvailableVersionCode})"
            : "Up To Date";
    }

    [JsonIgnoreAttribute] public int InstalledVersionCode { get; set; }
    [JsonIgnoreAttribute] public int AvailableVersionCode { get; set; }
    [JsonIgnoreAttribute] public bool IsUpdateAvailable { get; set; }
    [JsonIgnoreAttribute] public string UpdateStatus { get; set; }
}