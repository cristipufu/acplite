using System.Text.Json.Serialization;

namespace Acplite.Sdk.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(AgentProviderKind))]
[JsonSerializable(typeof(AcpProviderDescriptor))]
[JsonSerializable(typeof(IReadOnlyList<AcpProviderDescriptor>))]
[JsonSerializable(typeof(AcpDiscoveredSession))]
[JsonSerializable(typeof(IReadOnlyList<AcpDiscoveredSession>))]
[JsonSerializable(typeof(AcpSessionConfig))]
[JsonSerializable(typeof(AcpEvent))]
[JsonSerializable(typeof(AcpSessionStartedEvent))]
[JsonSerializable(typeof(AcpAssistantTextDeltaEvent))]
[JsonSerializable(typeof(AcpThinkingDeltaEvent))]
[JsonSerializable(typeof(AcpToolCallEvent))]
[JsonSerializable(typeof(AcpPermissionOption))]
[JsonSerializable(typeof(AcpPermissionRequestEvent))]
[JsonSerializable(typeof(AcpPlanEntry))]
[JsonSerializable(typeof(AcpPlanUpdateEvent))]
[JsonSerializable(typeof(AcpUsageSnapshot))]
[JsonSerializable(typeof(AcpUsageSnapshotEvent))]
[JsonSerializable(typeof(AcpTurnCompletedEvent))]
[JsonSerializable(typeof(AcpInterruptionKind))]
[JsonSerializable(typeof(AcpInterruptionClassification))]
[JsonSerializable(typeof(AcpInterruptionEvent))]
[JsonSerializable(typeof(AcpTranscriptHistoryEntryEvent))]
[JsonSerializable(typeof(JsonRpcRequestPayload))]
[JsonSerializable(typeof(JsonRpcNotificationPayload))]
[JsonSerializable(typeof(JsonRpcResponsePayload))]
[JsonSerializable(typeof(JsonRpcErrorPayload))]
[JsonSerializable(typeof(ClaudeControlRequestFrame))]
[JsonSerializable(typeof(ClaudeControlResponseFrame))]
[JsonSerializable(typeof(ClaudeControlInnerResponse))]
[JsonSerializable(typeof(ClaudeUserStreamFrame))]
[JsonSerializable(typeof(ClaudeUserMessageBody))]
[JsonSerializable(typeof(ClaudeContentItem))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
public partial class AcpJsonContext : JsonSerializerContext
{
}
