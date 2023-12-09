using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace QSideloader.Common;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UpdateInfo))]
[JsonSerializable(typeof(List<UpdateInfo>))]
public partial class CommonJsonSerializerContext : JsonSerializerContext
{
}