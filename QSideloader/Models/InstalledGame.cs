using Newtonsoft.Json;

namespace QSideloader.Models;

public class InstalledGame : Game
{
    [JsonIgnore] public int InstalledVersionCode { get; }
    [JsonIgnore] public string InstalledVersionName { get; }
    [JsonIgnore] public int AvailableVersionCode { get; }
    [JsonIgnore] public bool IsUpdateAvailable { get; } // This needed to make sorting work
    
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
    }
}