using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace QSideloader.Common;

public class UpdateInfo
{
    private const string UpdateInfoUrl = "https://qloader.5698452.xyz/files/updater/update_info.json";
    
    [JsonPropertyName("versions")] public List<VersionItem> VersionList { get; set; } = new();
    [JsonPropertyName("updater")] public List<UpdateAsset> Updater { get; set; } = new();
    
    public static async Task<UpdateInfo> GetInfoAsync()
    {
        using var client = new HttpClient();
        var response = await client.GetAsync(UpdateInfoUrl);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var updateInfo = JsonSerializer.Deserialize(json, CommonJsonSerializerContext.Default.UpdateInfo);
        return updateInfo ?? throw new Exception("Failed to deserialize update info list");
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