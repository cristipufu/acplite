using System.Diagnostics;
using System.Text.Json;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;

namespace Acplite.Sdk.Adapters;

internal sealed class AntigravitySessionHandle : AcpSessionHandleBase
{
    private long _controlRequestCounter;

    public AntigravitySessionHandle(
        AcpSessionConfig config,
        string sessionId,
        Stream agentOutputStream,
        Stream agentInputStream,
        Process? process)
        : base(AgentProviderKind.Antigravity, config, agentOutputStream, agentInputStream, process)
    {
        SessionId = sessionId;
        Transport.OnRawFrameAsync = HandleRawFrameAsync;
    }

    public override Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StartBackgroundMonitors();
        PublishEvent(new AcpSessionStartedEvent(
            Provider,
            SessionId,
            DateTimeOffset.UtcNow,
            Config.Model ?? "gemini",
            Config.WorkingDirectory));
        return Task.CompletedTask;
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

    public override Task RespondPermissionAsync(
        string requestId,
        bool approved,
        string? selectedOptionId = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

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

    private Task HandleRawFrameAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var incomingId = root.GetOptionalString("conversation_id") ?? root.GetOptionalString("session_id");
        if (!string.IsNullOrWhiteSpace(incomingId))
        {
            SessionId = incomingId!;
        }

        var type = root.GetOptionalString("type");
        switch (type)
        {
            case "PLANNER_RESPONSE":
            case "assistant":
            {
                var thinking = root.GetOptionalString("thinking");
                if (!string.IsNullOrEmpty(thinking))
                {
                    PublishEvent(new AcpThinkingDeltaEvent(Provider, SessionId, now, thinking));
                }

                var content = root.GetOptionalString("content");
                if (!string.IsNullOrEmpty(content))
                {
                    PublishEvent(new AcpAssistantTextDeltaEvent(Provider, SessionId, now, content));
                }
                else if (root.TryGetObject("message", out var msgObj))
                {
                    var extracted = ClaudeTranscriptParser.ExtractMessageText(msgObj);
                    if (!string.IsNullOrEmpty(extracted))
                    {
                        PublishEvent(new AcpAssistantTextDeltaEvent(Provider, SessionId, now, extracted));
                    }
                }
                break;
            }

            case "stream_event":
            {
                if (root.TryGetObject("event", out var ev) && ev.TryGetObject("delta", out var delta))
                {
                    var dt = delta.GetOptionalString("type");
                    if (dt == "text_delta")
                    {
                        var text = delta.GetOptionalString("text");
                        if (!string.IsNullOrEmpty(text))
                        {
                            PublishEvent(new AcpAssistantTextDeltaEvent(Provider, SessionId, now, text));
                        }
                    }
                    else if (dt == "thinking_delta")
                    {
                        var thinking = delta.GetOptionalString("thinking");
                        if (!string.IsNullOrEmpty(thinking))
                        {
                            PublishEvent(new AcpThinkingDeltaEvent(Provider, SessionId, now, thinking));
                        }
                    }
                }
                break;
            }

            case "result":
            {
                var isError = root.GetBooleanOrDefault("is_error");
                if (isError)
                {
                    var errText = root.GetStringOrDefault("result", "Antigravity error");
                    PublishClassifiedError(errText);
                }
                PublishEvent(new AcpTurnCompletedEvent(Provider, SessionId, now, isError ? "error" : "end_turn"));
                break;
            }
        }

        return Task.CompletedTask;
    }
}
