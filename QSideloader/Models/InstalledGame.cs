using Newtonsoft.Json;

namespace QSideloader.Models;

public class InstalledGame : Game
{
    public InstalledGame(Game game, int installedVersionCode = -1, string installedVersionName = "N/A")
    {
        GameName = game.GameName;
        ReleaseName = game.ReleaseName;
        PackageName = game.PackageName;
        AvailableVersionCode = game.VersionCode;
        VersionCode = game.VersionCode;
        LastUpdated = game.LastUpdated;
        GameSize = game.GameSize;
        InstalledVersionCode = installedVersionCode;
        InstalledVersionName = installedVersionName;
        IsUpdateAvailable = AvailableVersionCode > InstalledVersionCode;
        UpdateStatus = IsUpdateAvailable
            ? $"Update Available! ({InstalledVersionCode} -> {AvailableVersionCode})"
            : "Up To Date";
    }

    [JsonIgnore] public int InstalledVersionCode { get; set; }
    [JsonIgnore] public string InstalledVersionName { get; set; }
    [JsonIgnore] public int AvailableVersionCode { get; set; }
    [JsonIgnore] public bool IsUpdateAvailable { get; set; }
    [JsonIgnore] public string UpdateStatus { get; set; }
    
    public override bool Equals(object? obj)
    {
        return obj is InstalledGame game &&
               ReleaseName == game.ReleaseName &&
               InstalledVersionCode == game.InstalledVersionCode &&
               AvailableVersionCode == game.AvailableVersionCode;
    }

    public override int GetHashCode()
    {
        // ReSharper disable NonReadonlyMemberInGetHashCode
        return ReleaseName?.GetHashCode() ?? 0 + InstalledVersionCode.GetHashCode() + AvailableVersionCode.GetHashCode();
        // ReSharper restore NonReadonlyMemberInGetHashCode
    }
}