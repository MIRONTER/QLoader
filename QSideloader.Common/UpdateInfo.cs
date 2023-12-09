using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace QSideloader.Common;

public class UpdateInfo
{
    // private const string UpdateUrl = "https://qloader.5698452.xyz/api/v1/update_info";
    private const string UpdateUrl = "https://qloader.5698452.xyz/files/update_info.json"; // For testing
    [JsonPropertyName("version")] public Version Version { get; set; } = new();
    [JsonPropertyName("versionString")] public string VersionString { get; set; } = "";
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("checksum")] public string Checksum { get; set; } = "";
    [JsonPropertyName("releaseNotes")] public string ReleaseNotes { get; set; } = "";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    
    public static async Task<List<UpdateInfo>> GetUpdatesAsync()
    {
        using var client = new HttpClient();
        var response = await client.GetAsync(UpdateUrl);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var updateInfo = JsonSerializer.Deserialize(json, CommonJsonSerializerContext.Default.ListUpdateInfo);
        return updateInfo ?? throw new Exception("Failed to deserialize update info list");
    }
}