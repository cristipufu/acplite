using System.Text.Json.Serialization;

namespace Acplite.Pty;

public readonly record struct TerminalSize(int Columns, int Rows)
{
    public static TerminalSize Default => new(120, 36);

    public TerminalSize Clamp()
        => new(Math.Clamp(Columns, 20, 500), Math.Clamp(Rows, 5, 250));
}

public sealed record PtyStartOptions
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public TerminalSize InitialSize { get; init; } = TerminalSize.Default;
}

public sealed record PtySessionMetadata
{
    [JsonPropertyName("pid")]
    public required int Pid { get; init; }

    [JsonPropertyName("childPid")]
    public required int ChildPid { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("pipeName")]
    public required string PipeName { get; init; }

    [JsonPropertyName("workingDirectory")]
    public required string WorkingDirectory { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("transcriptPath")]
    public string? TranscriptPath { get; init; }

    [JsonPropertyName("startedAt")]
    public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("lastActiveAt")]
    public required DateTimeOffset LastActiveAt { get; init; }
}

public sealed record PtyPermissionOption(
    [property: JsonPropertyName("optionId")] string OptionId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("keystroke")] string Keystroke);

public sealed record PtyPendingPermission(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("rawInputJson")] string? RawInputJson,
    [property: JsonPropertyName("detectedAt")] DateTimeOffset DetectedAt,
    [property: JsonPropertyName("options")] IReadOnlyList<PtyPermissionOption> Options);

public static class PtyControlCommands
{
    public const string Ping = "ping";
    public const string GetStatus = "get_status";
    public const string SendPrompt = "send_prompt";
    public const string Steer = "steer";
    public const string RespondPermission = "respond_permission";
    public const string CancelTurn = "cancel_turn";
    public const string BindSession = "bind_session";
}

public static class PtyControlEventTypes
{
    public const string Hello = "hello";
    public const string Ack = "ack";
    public const string Error = "error";
    public const string PermissionRequest = "permission_request";
    public const string PermissionResolved = "permission_resolved";
    public const string SessionBound = "session_bound";
    public const string PromptInjected = "prompt_injected";
    public const string Exited = "exited";
}

public sealed class PtyControlRequest
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("requestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; init; }

    [JsonPropertyName("approved")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Approved { get; init; }

    [JsonPropertyName("selectedOptionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedOptionId { get; init; }

    [JsonPropertyName("sessionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    [JsonPropertyName("transcriptPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TranscriptPath { get; init; }
}

public sealed class PtyControlEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("requestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; init; }

    [JsonPropertyName("success")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Success { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PtySessionMetadata? Metadata { get; init; }

    [JsonPropertyName("permission")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PtyPendingPermission? Permission { get; init; }

    [JsonPropertyName("exitCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; init; }
}
