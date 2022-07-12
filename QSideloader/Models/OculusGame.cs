using System.Collections.Generic;
using Newtonsoft.Json;

namespace QSideloader.Models;

public class OculusGame
{
    [JsonProperty("id")]
    public string? Id { get; set; }
    [JsonProperty("store_name")]
    public string? StoreName { get; set; }
    [JsonProperty("url")]
    public string? Url { get; set; }
    [JsonProperty("display_name")]
    public string? DisplayName { get; set; }
    [JsonProperty("package_name")]
    public string? PackageName { get; set; }
    [JsonProperty("description")]
    public string? Description { get; set; }
    [JsonProperty("supported_hmd_platforms")]
    public List<string>? SupportedHmdPlatforms { get; set; }
    [JsonProperty("genre_names")]
    public List<string>? GenreNames { get; set; }
    [JsonProperty("developer_name")]
    public string? DeveloperName { get; set; }
    [JsonProperty("comfort_rating")]
    public string? ComfortRating { get; set; }
    [JsonProperty("quality_rating_aggregate")]
    public double QualityRatingAggregate { get; set; }
    [JsonProperty("rating_count")]
    public int RatingCount { get; set; }

    [JsonProperty("price")]
    public Dictionary<string, string> Price { get; set; } = new()
    {
        {"currency", "EUR"},
        {"formatted", "0,00€"},
        {"offset_amount", "0"}
    };
}