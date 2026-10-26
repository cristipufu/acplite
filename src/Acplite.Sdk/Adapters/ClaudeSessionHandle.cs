using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Acplite.Sdk.Formatting;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;

namespace Acplite.Sdk.Adapters;

internal sealed class ClaudeSessionHandle : AcpSessionHandleBase
{
    private readonly ConcurrentDictionary<string, string> _pendingPermissionRequests = new(StringComparer.Ordinal);
    private long _controlRequestCounter;

    public ClaudeSessionHandle(
        AcpSessionConfig config,
        Stream agentOutputStream,
        Stream agentInputStream,
        Process? process)
        : base(AgentProviderKind.Claude, config, agentOutputStream, agentInputStream, process)
    {
        Transport.OnRawFrameAsync = HandleRawFrameAsync;
    }

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StartBackgroundMonitors();
        await SendControlRequestAsync(
            "init",
            new Dictionary<string, string?> { ["subtype"] = "initialize" },
            cancellationToken).ConfigureAwait(false);
    }

    protected override async Task SendPromptCoreAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var frame = new ClaudeUserStreamFrame
        {
            SessionId = SessionId,
            Message = new ClaudeUserMessageBody
            {
                Role = "user",
                Content = [new ClaudeContentItem { Type = "text", Text = prompt }]
            }
        };

        var json = JsonSerializer.Serialize(frame, AcpJsonContext.Default.ClaudeUserStreamFrame);
        await Transport.SendRawJsonLineAsync(json, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<bool> SteerAsync(string steeringPrompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(steeringPrompt))
        {
            return false;
        }

        if (IsTurnActive)
        {
            await SendPromptCoreAsync(steeringPrompt, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SendPromptAsync(steeringPrompt, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public override async Task RespondPermissionAsync(
        string requestId,
        bool approved,
        string? selectedOptionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        _pendingPermissionRequests.TryRemove(requestId, out _);

        var responseDict = new Dictionary<string, string?>
        {
            ["behavior"] = approved ? "allow" : "deny"
        };

        if (!approved)
        {
            responseDict["message"] = "Tool execution denied by user.";
        }

        var responseFrame = new ClaudeControlResponseFrame
        {
            Response = new ClaudeControlInnerResponse
            {
                Subtype = "success",
                RequestId = requestId,
                Response = responseDict
            }
        };

        var json = JsonSerializer.Serialize(responseFrame, AcpJsonContext.Default.ClaudeControlResponseFrame);
        await Transport.SendRawJsonLineAsync(json, cancellationToken).ConfigureAwait(false);
    }

    public override Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return SendControlRequestAsync(
            "model",
            new Dictionary<string, string?>
            {
                ["subtype"] = "set_model",
                ["model"] = modelId
            },
            cancellationToken);
    }

    public override Task CancelTurnAsync(CancellationToken cancellationToken = default)
        => SendControlRequestAsync(
            "cancel",
            new Dictionary<string, string?> { ["subtype"] = "interrupt" },
            cancellationToken);

    protected override void OnSessionFaulted(Exception exception)
    {
        _pendingPermissionRequests.Clear();
    }

    private async Task SendControlRequestAsync(
        string prefix,
        Dictionary<string, string?> requestFields,
        CancellationToken cancellationToken)
    {
        var reqId = $"{prefix}_{Interlocked.Increment(ref _controlRequestCounter)}";
        var frame = new ClaudeControlRequestFrame
        {
            RequestId = reqId,
            Request = requestFields
        };

        var json = JsonSerializer.Serialize(frame, AcpJsonContext.Default.ClaudeControlRequestFrame);
        await Transport.SendRawJsonLineAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleRawFrameAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var type = root.GetOptionalString("type");
        if (string.IsNullOrEmpty(type))
        {
            return;
        }

        var incomingSessionId = root.GetOptionalString("session_id");
        if (!string.IsNullOrWhiteSpace(incomingSessionId))
        {
            SessionId = incomingSessionId!;
        }

        var now = DateTimeOffset.UtcNow;

        switch (type)
        {
            case "system":
                HandleSystemFrame(root, now);
                break;

            case "stream_event":
                HandleStreamEventFrame(root, now);
                break;

            case "control_request":
                await HandleControlRequestFrameAsync(root, now, cancellationToken).ConfigureAwait(false);
                break;

            case "result":
                HandleResultFrame(root, now);
                break;
        }
    }

    private void HandleSystemFrame(JsonElement root, DateTimeOffset now)
    {
        if (root.GetOptionalString("subtype") == "init")
        {
            var model = root.GetOptionalString("model") ?? Config.Model;
            var cwd = root.GetOptionalString("cwd") ?? Config.WorkingDirectory;
            PublishEvent(new AcpSessionStartedEvent(Provider, SessionId, now, model, cwd));
        }
    }

    private void HandleStreamEventFrame(JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetObject("event", out var ev))
        {
            return;
        }

        var evType = ev.GetOptionalString("type");
        if (evType == "content_block_delta" && ev.TryGetObject("delta", out var delta))
        {
            var deltaType = delta.GetOptionalString("type");
            if (deltaType == "text_delta")
            {
                var text = delta.GetOptionalString("text");
                if (!string.IsNullOrEmpty(text))
                {
                    PublishEvent(new AcpAssistantTextDeltaEvent(Provider, SessionId, now, text));
                }
            }
            else if (deltaType == "thinking_delta")
            {
                var thinking = delta.GetOptionalString("thinking");
                if (!string.IsNullOrEmpty(thinking))
                {
                    PublishEvent(new AcpThinkingDeltaEvent(Provider, SessionId, now, thinking));
                }
            }
        }
        else if (evType == "content_block_start" &&
                 ev.TryGetObject("content_block", out var cb) &&
                 cb.GetOptionalString("type") == "tool_use")
        {
            var toolId = cb.GetStringOrDefault("id", Guid.NewGuid().ToString("N"));
            var toolName = cb.GetStringOrDefault("name", AcpRoles.Tool);
            PublishEvent(new AcpToolCallEvent(
                Provider: Provider,
                SessionId: SessionId,
                Timestamp: now,
                ToolCallId: toolId,
                ToolName: toolName,
                Title: toolName,
                Kind: TextSanitizer.InferToolKind(toolName),
                Status: AcpToolStatuses.InProgress));
        }
    }

    private async Task HandleControlRequestFrameAsync(JsonElement root, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reqId = root.GetOptionalString("request_id");
        if (string.IsNullOrEmpty(reqId) || !root.TryGetObject("request", out var reqObj))
        {
            return;
        }

        if (reqObj.GetOptionalString("subtype") != "can_use_tool")
        {
            return;
        }

        var toolName = reqObj.GetStringOrDefault("tool_name", AcpRoles.Tool);
        _pendingPermissionRequests[reqId] = toolName;

        if (Config.AutoApproveTools)
        {
            await RespondPermissionAsync(reqId, approved: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        var rawInputJson = reqObj.TryGetProperty("input", out var inp) && inp.ValueKind != JsonValueKind.Undefined
            ? inp.GetRawText()
            : null;

        PublishEvent(new AcpPermissionRequestEvent(
            Provider: Provider,
            SessionId: SessionId,
            Timestamp: now,
            RequestId: reqId,
            ToolName: toolName,
            Title: $"Allow {toolName}?",
            Description: reqObj.GetOptionalString("description"),
            RawInputJson: rawInputJson,
            Options: AcpPermissionOptions.DefaultAllowDeny));
    }

    private void HandleResultFrame(JsonElement root, DateTimeOffset now)
    {
        var isError = root.GetBooleanOrDefault("is_error");
        var subtype = root.GetStringOrDefault("subtype", AcpStopReasons.EndTurn);
        var durationMs = root.GetOptionalInt64("duration_ms");
        var costUsd = root.GetOptionalDecimal("total_cost_usd");

        long? inputTokens = null;
        long? outputTokens = null;
        if (root.TryGetObject("usage", out var usage))
        {
            inputTokens = usage.GetOptionalInt64("input_tokens");
            outputTokens = usage.GetOptionalInt64("output_tokens");
        }

        if (isError)
        {
            var resultText = root.GetOptionalString("result") ?? subtype;
            PublishClassifiedError(resultText, subtype);
        }

        PublishEvent(new AcpTurnCompletedEvent(
            Provider: Provider,
            SessionId: SessionId,
            Timestamp: now,
            StopReason: isError ? AcpStopReasons.Error : AcpStopReasons.EndTurn,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CostUsd: costUsd,
            DurationMs: durationMs));
    }
}
