using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acplite.Sdk.Protocol;

[JsonConverter(typeof(JsonStringEnumConverter<AgentProviderKind>))]
public enum AgentProviderKind
{
    Claude,
    Codex,
    Grok,
    Antigravity,
    GenericAcp
}

public static class AgentProviderParser
{
    public static bool TryParse(string? value, out AgentProviderKind kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            kind = default;
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "claude":
            case "claude-code":
            case "claudecode":
                kind = AgentProviderKind.Claude;
                return true;
            case "codex":
            case "openai":
                kind = AgentProviderKind.Codex;
                return true;
            case "grok":
            case "xai":
                kind = AgentProviderKind.Grok;
                return true;
            case "agy":
            case "antigravity":
            case "gemini":
                kind = AgentProviderKind.Antigravity;
                return true;
            case "acp":
            case "generic":
            case "generic-acp":
                kind = AgentProviderKind.GenericAcp;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public static string ToShortId(this AgentProviderKind kind) => kind switch
    {
        AgentProviderKind.Claude => "claude",
        AgentProviderKind.Codex => "codex",
        AgentProviderKind.Grok => "grok",
        AgentProviderKind.Antigravity => "agy",
        AgentProviderKind.GenericAcp => "acp",
        _ => "acp"
    };

    public static string ToDisplayName(this AgentProviderKind kind) => kind switch
    {
        AgentProviderKind.Claude => "Claude Code",
        AgentProviderKind.Codex => "OpenAI Codex",
        AgentProviderKind.Grok => "xAI Grok",
        AgentProviderKind.Antigravity => "Google Antigravity",
        AgentProviderKind.GenericAcp => "Generic ACP",
        _ => "Generic ACP"
    };
}

public sealed record AcpProviderDescriptor(
    AgentProviderKind Provider,
    string ShortId,
    string DisplayName,
    string BinaryName,
    bool IsInstalled,
    string? ResolvedBinaryPath,
    string? Version,
    bool IsNativeAcp,
    bool SupportsLiveSteering,
    bool SupportsSessionDiscovery,
    bool SupportsUsageProbe);

public sealed record AcpDiscoveredSession(
    AgentProviderKind Provider,
    string SessionId,
    string Title,
    string? WorkingDirectory,
    string TranscriptPath,
    DateTimeOffset UpdatedAt,
    DateTimeOffset CreatedAt,
    string? Model = null,
    string? GitBranch = null,
    bool IsActive = false,
    int MessageCount = 0);

public sealed record AcpSessionConfig
{
    public required AgentProviderKind Provider { get; init; }
    public required string WorkingDirectory { get; init; }
    public string? SessionId { get; init; }
    public bool ResumeExisting { get; init; }
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? PermissionMode { get; init; }
    public bool AutoApproveTools { get; init; }
    public string? BinaryPathOverride { get; init; }
    public IReadOnlyList<string>? ExtraArguments { get; init; }
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Represents a user prompt queued for execution on a subsequent turn once the active turn finishes.
/// </summary>
public sealed record AcpQueuedPromptItem(
    string QueueItemId,
    string Prompt,
    DateTimeOffset EnqueuedAt);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AcpSessionStartedEvent), "session_started")]
[JsonDerivedType(typeof(AcpAssistantTextDeltaEvent), "assistant_text_delta")]
[JsonDerivedType(typeof(AcpThinkingDeltaEvent), "thinking_delta")]
[JsonDerivedType(typeof(AcpToolCallEvent), "tool_call")]
[JsonDerivedType(typeof(AcpPermissionRequestEvent), "permission_request")]
[JsonDerivedType(typeof(AcpPlanUpdateEvent), "plan_update")]
[JsonDerivedType(typeof(AcpUsageSnapshotEvent), "usage_snapshot")]
[JsonDerivedType(typeof(AcpTurnCompletedEvent), "turn_completed")]
[JsonDerivedType(typeof(AcpInterruptionEvent), "interruption")]
[JsonDerivedType(typeof(AcpPromptQueueUpdatedEvent), "prompt_queue_updated")]
[JsonDerivedType(typeof(AcpTranscriptHistoryEntryEvent), "transcript_history_entry")]
public abstract record AcpEvent(
    AgentProviderKind Provider,
    string SessionId,
    DateTimeOffset Timestamp);

public sealed record AcpSessionStartedEvent(
    AgentProviderKind Provider,
    string SessionId,
    DateTimeOffset Timestamp,
    string? Model,
    string? WorkingDirectory) : AcpEvent(Provider, SessionId, Timestamp);

public sealed record AcpAssistantTextDeltaEvent(
    AgentProviderKind Provider,
    string SessionId,
    DateTimeOffset Timestamp,
    string Delta,
    string? MessageId = null) : AcpEvent(Provider, SessionId, Timestamp);

public sealed record AcpThinkingDeltaEvent(
    AgentProviderKind Provider,
    string SessionId,
    DateTimeOffset Timestamp,
    string Delta) : AcpEvent(Provider, SessionId, Timestamp);

public sealed record AcpToolCallEvent(
    AgentProviderKind Provider,
    string SessionId,
    DateTimeOffset Timestamp,
    string ToolCallId,
    string ToolName,
    string Title,
    string Kind,
    string Status,
    string? RawInputJson = null,
    string? RawOutput = null,
    IReadOnlyList<string>? FilePaths = null) : AcpEvent(Provider, SessionId, Timestamp);

public sealed record AcpPermissionOption(
    string OptionId,
    string Name,
    string Kind);

public sealed record AcpPermissionRequestEvent(
    AgentProviderKind Provider,
    string SessionId,
    DateTimeOffset Timestamp,
    string RequestId,
    string ToolName,
    string Title,
    string? Description,
    string? RawInputJson,
    IReadOnlyList<AcpPermissionOption> Options) : AcpEvent(Provider, SessionId, Timestamp);

public sealed record AcpPlanEntry(
    string Content,
    string Status,
    string? Priority = null);

public sealed record AcpPlanUpdateEvent(
    AgentProviderKind Provider,
    string SessionId,
    DateTimeOffset Timestamp,
    IReadOnlyList<AcpPlanEntry> Entries) : AcpEvent(Provider, SessionId, Timestamp);

public sealed record AcpUsageSnapshot(
    AgentProviderKind Provider,
    DateTimeOffset CapturedAt,
    string? PlanType = null,
    double? FiveHourPercentUsed = null,
    DateTimeOffset? FiveHourResetsAt = null,
    double? SevenDayPercentUsed = null,
    DateTimeOffset? SevenDayResetsAt = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    decimal? CreditsRemainingUsd = null,
    string? SummaryText = null);

public sealed record AcpUsageSnapshotEvent(
    AgentProviderKind Provider,
    string SessionId,
    DateTimeOffset Timestamp,
    AcpUsageSnapshot Snapshot) : AcpEvent(Provider, SessionId, Timestamp);

public sealed record AcpTurnCompletedEvent(
    AgentProviderKind Provider,
    string SessionId,
    DateTimeOffset Timestamp,
    string StopReason,
    long? InputTokens = null,
    long? OutputTokens = null,
    decimal? CostUsd = null,
    long? DurationMs = null) : AcpEvent(Provider, SessionId, Timestamp);

[JsonConverter(typeof(JsonStringEnumConverter<AcpInterruptionKind>))]
public enum AcpInterruptionKind
{
    None,
    TransientCapacity,
    RateLimitQuota,
    ContextWindowExceeded,
    AuthenticationRequired,
    PermissionDenied,
    ProcessCrashed,
    UnknownError
}

public sealed record AcpInterruptionClassification(
    AcpInterruptionKind Kind,
    bool IsRetryable,
    string Message,
    string? RawCode = null,
    TimeSpan? RecommendedRetryDelay = null,
    DateTimeOffset? QuotaResetsAt = null);

public sealed record AcpInterruptionEvent(
    AgentProviderKind Provider,
    string SessionId,
    DateTimeOffset Timestamp,
    AcpInterruptionClassification Classification) : AcpEvent(Provider, SessionId, Timestamp);

public sealed record AcpPromptQueueUpdatedEvent(
    AgentProviderKind Provider,
    string SessionId,
    DateTimeOffset Timestamp,
    IReadOnlyList<AcpQueuedPromptItem> QueuedPrompts) : AcpEvent(Provider, SessionId, Timestamp);

public sealed record AcpTranscriptHistoryEntryEvent(
    AgentProviderKind Provider,
    string SessionId,
    DateTimeOffset Timestamp,
    string Role,
    string Text,
    string? Thinking = null,
    string? ToolName = null,
    string? ToolCallId = null) : AcpEvent(Provider, SessionId, Timestamp);

// --- Wire DTOs for Source-Generated JSON-RPC 2.0 & Provider Streams ---

public sealed class JsonRpcRequestPayload
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("method")]
    public required string Method { get; set; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }
}

public sealed class JsonRpcNotificationPayload
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public required string Method { get; set; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }
}

public sealed class JsonRpcResponsePayload
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcErrorPayload? Error { get; set; }
}

public sealed class JsonRpcErrorPayload
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; set; }
}

public sealed class ClaudeControlRequestFrame
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "control_request";

    [JsonPropertyName("request_id")]
    public required string RequestId { get; set; }

    [JsonPropertyName("request")]
    public required Dictionary<string, string?> Request { get; set; }
}

public sealed class ClaudeControlResponseFrame
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "control_response";

    [JsonPropertyName("response")]
    public required ClaudeControlInnerResponse Response { get; set; }
}

public sealed class ClaudeControlInnerResponse
{
    [JsonPropertyName("subtype")]
    public string Subtype { get; set; } = "success";

    [JsonPropertyName("request_id")]
    public required string RequestId { get; set; }

    [JsonPropertyName("response")]
    public required Dictionary<string, string?> Response { get; set; }
}

public sealed class ClaudeUserStreamFrame
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "user";

    [JsonPropertyName("session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; set; }

    [JsonPropertyName("message")]
    public required ClaudeUserMessageBody Message { get; set; }
}

public sealed class ClaudeUserMessageBody
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public required List<ClaudeContentItem> Content { get; set; }
}

public sealed class ClaudeContentItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public required string Text { get; set; }
}
