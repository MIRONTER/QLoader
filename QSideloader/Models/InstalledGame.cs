using Newtonsoft.Json;

namespace QSideloader.Models;

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

    [JsonIgnore] public int InstalledVersionCode { get; set; }
    [JsonIgnore] public int AvailableVersionCode { get; set; }
    [JsonIgnore] public bool IsUpdateAvailable { get; set; }
    [JsonIgnore] public string UpdateStatus { get; set; }
}