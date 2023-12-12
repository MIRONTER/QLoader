using System.Text.Json.Serialization;

namespace QSideloader.Common;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UpdateInfo))]
public partial class CommonJsonSerializerContext : JsonSerializerContext;