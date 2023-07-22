using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace QSideloader.Models;

public class OculusGame
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("store_name")] public string? StoreName { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("package_name")] public string? PackageName { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    [JsonPropertyName("supported_hmd_platforms")]
    public List<string>? SupportedHmdPlatforms { get; set; }

    [JsonPropertyName("genre_names")] public List<string>? GenreNames { get; set; }
    [JsonPropertyName("developer_name")] public string? DeveloperName { get; set; }
    [JsonPropertyName("comfort_rating")] public string? ComfortRating { get; set; }

    [JsonPropertyName("quality_rating_aggregate")]
    public double QualityRatingAggregate { get; set; }

    [JsonPropertyName("rating_count")] public int RatingCount { get; set; }

    [JsonPropertyName("price")]
    public Dictionary<string, string> Price { get; set; } = new()
    {
        {"currency", "EUR"},
        {"formatted", "0,00€"},
        {"offset_amount", "0"}
    };
}