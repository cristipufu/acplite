using System.Text.Json.Serialization;

namespace Acplite.Pty;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PtySessionMetadata))]
[JsonSerializable(typeof(IReadOnlyList<PtySessionMetadata>))]
[JsonSerializable(typeof(PtyPermissionOption))]
[JsonSerializable(typeof(IReadOnlyList<PtyPermissionOption>))]
[JsonSerializable(typeof(PtyPendingPermission))]
[JsonSerializable(typeof(PtyControlRequest))]
[JsonSerializable(typeof(PtyControlEvent))]
public partial class PtyJsonContext : JsonSerializerContext
{
}
