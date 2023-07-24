using System.Collections.Generic;
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
public partial class JsonSerializerContext : System.Text.Json.Serialization.JsonSerializerContext
{
}