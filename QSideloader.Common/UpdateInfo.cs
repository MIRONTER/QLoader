using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Serilog;

namespace QSideloader.Common;

public class UpdateInfo
{
    private const string UpdateInfoUrl = "https://qloader.5698452.xyz/files/updater/update_info.json";
    
    [JsonPropertyName("versions")] public List<VersionItem> VersionList { get; set; } = [];
    [JsonPropertyName("updater")] public List<UpdateAsset> Updater { get; set; } = [];
    
    public static async Task<UpdateInfo> GetInfoAsync()
    {
        using var client = new HttpClient();
        var response = await client.GetAsync(UpdateInfoUrl);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var updateInfo = JsonSerializer.Deserialize(json, CommonJsonSerializerContext.Default.UpdateInfo);
        return updateInfo ?? throw new Exception("Failed to deserialize update info list");
    }
    
    public UpdateAsset? GetUpdaterAsset()
    {
        var osName = GetOsName();
        return osName is null ? null : Updater.FirstOrDefault(asset => asset.Os == osName);
    }
    
    public VersionItem? GetLatestVersion(Version? currentVersion, string channel = "stable")
    {
        if (GetOsName() is null)
        {
            Log.Warning("Unsupported OS, cannot get latest version");
            return null;
        }
        currentVersion ??= new Version(0, 0, 0, 0);
        return VersionList.FirstOrDefault(versionItem =>
            versionItem.Channel == channel && versionItem.Version > currentVersion);
    }
    
    private static string? GetOsName()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 when OperatingSystem.IsWindows() => "win-x64",
            Architecture.X64 when OperatingSystem.IsLinux() => "linux-x64",
            Architecture.Arm64 when OperatingSystem.IsLinux() => "linux-arm64",
            Architecture.X64 when OperatingSystem.IsMacOS() => "osx-x64",
            Architecture.Arm64 when OperatingSystem.IsMacOS() => "osx-arm64",
            _ => null
        };
    }
}

public class VersionItem
{
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("versionString")] public string VersionString { get; set; } = "";
    [JsonPropertyName("version")] public Version Version { get; set; } = new();
    [JsonPropertyName("assets")] public List<UpdateAsset> Assets { get; set; } = [];
    [JsonPropertyName("releaseNotes")] public string ReleaseNotes { get; set; } = "";
}


public class UpdateAsset
{
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}