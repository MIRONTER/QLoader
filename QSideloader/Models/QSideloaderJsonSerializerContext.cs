using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using QSideloader.ViewModels;

namespace QSideloader.Models;

[JsonSerializable(typeof(SettingsData))]
[JsonSerializable(typeof(OculusGame))]
[JsonSerializable(typeof(Game))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<Dictionary<string, JsonElement>>))]
public partial class QSideloaderJsonSerializerContext : JsonSerializerContext
{
    public static JsonSerializerContext Indented { get; } = new QSideloaderJsonSerializerContext(new JsonSerializerOptions
    {
        WriteIndented = true
    });
}

