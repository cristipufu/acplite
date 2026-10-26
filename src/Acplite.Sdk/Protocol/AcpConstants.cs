namespace Acplite.Sdk.Protocol;

public static class AcpRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
    public const string Tool = "tool";
}

public static class AcpToolStatuses
{
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public static class AcpToolKinds
{
    public const string Execute = "execute";
    public const string Edit = "edit";
    public const string Read = "read";
    public const string Other = "other";
    public const string Command = "command";
    public const string Thought = "thought";
}

public static class AcpStopReasons
{
    public const string EndTurn = "end_turn";
    public const string Cancelled = "cancelled";
    public const string Canceled = "canceled";
    public const string Interrupted = "interrupted";
    public const string Error = "error";
    public const string MaxTokens = "max_tokens";

    public static bool IsFailureOrCancellation(string? stopReason)
    {
        if (string.IsNullOrWhiteSpace(stopReason))
        {
            return false;
        }

        return stopReason.Equals(Error, StringComparison.OrdinalIgnoreCase) ||
               stopReason.Equals(Cancelled, StringComparison.OrdinalIgnoreCase) ||
               stopReason.Equals(Canceled, StringComparison.OrdinalIgnoreCase) ||
               stopReason.Equals(Interrupted, StringComparison.OrdinalIgnoreCase);
    }
}

public static class AcpPermissionOptions
{
    public const string AllowId = "allow";
    public const string DenyId = "deny";
    public const string AcceptId = "accept";
    public const string DeclineId = "decline";

    public const string AllowOnceKind = "allow_once";
    public const string AllowAlwaysKind = "allow_always";
    public const string RejectOnceKind = "reject_once";

    public static IReadOnlyList<AcpPermissionOption> DefaultAllowDeny { get; } =
    [
        new AcpPermissionOption(AllowId, "Allow", AllowOnceKind),
        new AcpPermissionOption(DenyId, "Deny", RejectOnceKind)
    ];

    public static IReadOnlyList<AcpPermissionOption> DefaultAcceptDecline { get; } =
    [
        new AcpPermissionOption(AcceptId, "Accept", AllowOnceKind),
        new AcpPermissionOption(DeclineId, "Decline", RejectOnceKind)
    ];
}

public static class AcpEventKinds
{
    public const string SessionStarted = "session_started";
    public const string AssistantTextDelta = "assistant_text_delta";
    public const string ThinkingDelta = "thinking_delta";
    public const string ToolCall = "tool_call";
    public const string PermissionRequest = "permission_request";
    public const string PlanUpdate = "plan_update";
    public const string UsageSnapshot = "usage_snapshot";
    public const string TurnCompleted = "turn_completed";
    public const string Interruption = "interruption";
    public const string PromptQueueUpdated = "prompt_queue_updated";
    public const string TranscriptHistoryEntry = "transcript_history_entry";
}

public static class AcpRpcMethods
{
    public const string Initialize = "initialize";
    public const string SessionNew = "session/new";
    public const string SessionLoad = "session/load";
    public const string SessionPrompt = "session/prompt";
    public const string SessionUpdate = "session/update";
    public const string XaiSessionUpdate = "_x.ai/session/update";
    public const string SessionCancel = "session/cancel";
    public const string SessionSetModel = "session/set_model";
    public const string SessionRequestPermission = "session/request_permission";

    public const string SessionSteering = "_session/steering";
    public const string XaiInterject = "_x.ai/interject";

    public const string SessionQueue = "session/queue";
    public const string LegacySessionQueue = "_session/queue";
    public const string SessionQueueRemove = "session/queue_remove";
    public const string LegacySessionQueueRemove = "_session/queue/remove";
    public const string SessionQueueClear = "session/queue_clear";
    public const string LegacySessionQueueClear = "_session/queue/clear";
    public const string SessionInterruptAndPrompt = "session/interrupt_and_prompt";
    public const string LegacySessionInterruptAndPrompt = "_session/interrupt_and_prompt";
}

public static class AcpSessionUpdateKinds
{
    public const string AgentMessageChunk = "agent_message_chunk";
    public const string AgentThoughtChunk = "agent_thought_chunk";
    public const string ToolCall = "tool_call";
    public const string ToolCallUpdate = "tool_call_update";
    public const string PromptQueueUpdate = "prompt_queue_update";
}
