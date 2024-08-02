using System.Text.Json.Serialization;

namespace QSideloader.Models;

public class DownloadedGame(Game game, string path, long size, int? latestVersionCode) :
    Game(game.GameName, game.ReleaseName, game.PackageName)
{
    [JsonIgnore] public string Path { get; } = path;
    [JsonIgnore] public long Size { get; } = size;
    [JsonIgnore] public bool? IsUpdateAvailable { get; } = latestVersionCode is null ? null : latestVersionCode > game.VersionCode;
    [JsonIgnore] public int? InstalledVersionCode { get; } = game.VersionCode;
    [JsonIgnore] public int? AvailableVersionCode { get; } = latestVersionCode;
    
    public override void Install()
    {
        InstallFromPath(Path);
    }
}