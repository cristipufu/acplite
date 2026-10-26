using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Acplite.Sdk.Formatting;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;

namespace Acplite.Sdk.Adapters;

internal sealed class CodexSessionHandle : AcpSessionHandleBase
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(bool Approved, string? OptionId)>> _pendingApprovals = new(StringComparer.Ordinal);
    private string? _activeTurnId;
    private string? _model;

    public CodexSessionHandle(
        AcpSessionConfig config,
        Stream agentOutputStream,
        Stream agentInputStream,
        Process? process)
        : base(AgentProviderKind.Codex, config, agentOutputStream, agentInputStream, process)
    {
        _model = config.Model;
        Transport.OnNotificationAsync = HandleNotificationAsync;
        Transport.OnRequestAsync = HandleServerRequestAsync;
    }

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StartBackgroundMonitors();

        var initParams = JsonElementBuilder.Build(static w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("clientInfo");
            w.WriteStartObject();
            w.WriteString("name", "acplite");
            w.WriteString("title", "Acplite.Sdk");
            w.WriteString("version", "0.1.2");
            w.WriteEndObject();
            w.WriteEndObject();
        });

        await Transport.SendRequestAsync("initialize", initParams, cancellationToken).ConfigureAwait(false);
        await Transport.SendNotificationAsync("initialized", null, cancellationToken).ConfigureAwait(false);

        if (Config.ResumeExisting && !string.IsNullOrWhiteSpace(Config.SessionId))
        {
            var resumeParams = JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("threadId", Config.SessionId);
                w.WriteString("cwd", Config.WorkingDirectory);
                if (!string.IsNullOrWhiteSpace(_model)) w.WriteString("model", _model);
                w.WriteEndObject();
            });

            var res = await Transport.SendRequestAsync("thread/resume", resumeParams, cancellationToken).ConfigureAwait(false);
            UpdateThreadIdFromResponse(res);
        }
        else
        {
            var startParams = JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("cwd", Config.WorkingDirectory);
                if (!string.IsNullOrWhiteSpace(_model)) w.WriteString("model", _model);
                w.WriteString("approvalPolicy", Config.AutoApproveTools ? "never" : "on-request");
                w.WriteEndObject();
            });

            var res = await Transport.SendRequestAsync("thread/start", startParams, cancellationToken).ConfigureAwait(false);
            UpdateThreadIdFromResponse(res);
        }

        PublishEvent(new AcpSessionStartedEvent(
            Provider,
            SessionId,
            DateTimeOffset.UtcNow,
            _model,
            Config.WorkingDirectory));
    }

    protected override async Task SendPromptCoreAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var turnParams = JsonElementBuilder.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("threadId", SessionId);
            w.WritePropertyName("input");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", prompt);
            w.WriteEndObject();
            w.WriteEndArray();
            if (!string.IsNullOrWhiteSpace(_model)) w.WriteString("model", _model);
            if (!string.IsNullOrWhiteSpace(Config.ReasoningEffort)) w.WriteString("effort", Config.ReasoningEffort);
            w.WriteEndObject();
        });

        var result = await Transport.SendRequestAsync("turn/start", turnParams, cancellationToken).ConfigureAwait(false);
        if (result.TryGetObject("turn", out var turn))
        {
            var turnId = turn.GetOptionalString("id");
            if (!string.IsNullOrWhiteSpace(turnId))
            {
                Volatile.Write(ref _activeTurnId, turnId);
            }
        }
    }

    public override async Task<bool> SteerAsync(string steeringPrompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(steeringPrompt))
        {
            return false;
        }

        var turnId = Volatile.Read(ref _activeTurnId);
        if (string.IsNullOrWhiteSpace(turnId))
        {
            await SendPromptAsync(steeringPrompt, cancellationToken).ConfigureAwait(false);
            return true;
        }

        try
        {
            var steerParams = JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("threadId", SessionId);
                w.WriteString("expectedTurnId", turnId);
                w.WritePropertyName("input");
                w.WriteStartArray();
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", steeringPrompt);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            });

            await Transport.SendRequestAsync("turn/steer", steerParams, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public override Task RespondPermissionAsync(
        string requestId,
        bool approved,
        string? selectedOptionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        if (_pendingApprovals.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult((approved, selectedOptionId));
        }
        return Task.CompletedTask;
    }

    public override Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        _model = modelId;
        return Task.CompletedTask;
    }

    public override async Task CancelTurnAsync(CancellationToken cancellationToken = default)
    {
        var turnId = Volatile.Read(ref _activeTurnId);
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        var interruptParams = JsonElementBuilder.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("threadId", SessionId);
            w.WriteString("turnId", turnId);
            w.WriteEndObject();
        });

        await Transport.SendRequestAsync("turn/interrupt", interruptParams, cancellationToken).ConfigureAwait(false);
    }

    protected override void OnSessionFaulted(Exception exception)
    {
        foreach (var (key, _) in _pendingApprovals)
        {
            if (_pendingApprovals.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(exception);
            }
        }
    }

    private void UpdateThreadIdFromResponse(JsonElement response)
    {
        if (response.TryGetObject("thread", out var thread))
        {
            var id = thread.GetOptionalString("id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                SessionId = id!;
            }
        }
    }

    private Task HandleNotificationAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (!parameters.HasValue || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return Task.CompletedTask;
        }

        var p = parameters.Value;
        var now = DateTimeOffset.UtcNow;

        switch (method)
        {
            case "turn/started":
            {
                if (p.TryGetObject("turn", out var turn))
                {
                    var turnId = turn.GetOptionalString("id");
                    if (!string.IsNullOrWhiteSpace(turnId))
                    {
                        Volatile.Write(ref _activeTurnId, turnId);
                    }
                }
                break;
            }

            case "item/agentMessage/delta":
            {
                var delta = p.GetOptionalString("delta");
                if (!string.IsNullOrEmpty(delta))
                {
                    PublishEvent(new AcpAssistantTextDeltaEvent(Provider, SessionId, now, delta, p.GetOptionalString("itemId")));
                }
                break;
            }

            case "item/reasoning/summaryTextDelta":
            case "item/reasoning/textDelta":
            {
                var delta = p.GetOptionalString("delta");
                if (!string.IsNullOrEmpty(delta))
                {
                    PublishEvent(new AcpThinkingDeltaEvent(Provider, SessionId, now, delta));
                }
                break;
            }

            case "item/started":
            case "item/completed":
                HandleItemNotification(method, p, now);
                break;

            case "turn/completed":
                HandleTurnCompletedNotification(p, now);
                break;

            case "error":
            {
                if (p.TryGetObject("error", out var err))
                {
                    PublishClassifiedError(err.GetStringOrDefault("message", "Codex error"));
                }
                break;
            }
        }

        return Task.CompletedTask;
    }

    private void HandleItemNotification(string method, JsonElement p, DateTimeOffset now)
    {
        if (!p.TryGetObject("item", out var item))
        {
            return;
        }

        var itemType = item.GetOptionalString("type");
        if (itemType is not ("commandExecution" or "fileChange" or "mcpToolCall"))
        {
            return;
        }

        var toolId = item.GetStringOrDefault("id", Guid.NewGuid().ToString("N"));
        var status = method == "item/started" ? "in_progress" : "completed";
        var title = itemType == "commandExecution"
            ? item.GetStringOrDefault("command", itemType)
            : itemType;

        PublishEvent(new AcpToolCallEvent(
            Provider: Provider,
            SessionId: SessionId,
            Timestamp: now,
            ToolCallId: toolId,
            ToolName: itemType,
            Title: title,
            Kind: TextSanitizer.InferToolKind(itemType),
            Status: status,
            RawOutput: item.GetOptionalString("aggregatedOutput")));
    }

    private void HandleTurnCompletedNotification(JsonElement p, DateTimeOffset now)
    {
        var status = "end_turn";
        if (p.TryGetObject("turn", out var turn))
        {
            status = turn.GetStringOrDefault("status", "end_turn");
            if (turn.TryGetObject("error", out var err))
            {
                PublishClassifiedError(err.GetStringOrDefault("message", "Codex turn error"));
            }
        }

        Volatile.Write(ref _activeTurnId, null);
        PublishEvent(new AcpTurnCompletedEvent(Provider, SessionId, now, status));
    }

    private async Task<JsonElement?> HandleServerRequestAsync(
        JsonElement idElement,
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        if (method is not ("item/commandExecution/requestApproval" or "item/fileChange/requestApproval"))
        {
            return JsonElementBuilder.EmptyObject;
        }

        var reqId = idElement.ValueKind == JsonValueKind.String ? idElement.GetString()! : idElement.GetRawText();
        if (Config.AutoApproveTools)
        {
            return BuildDecisionPayload(accepted: true);
        }

        var tcs = new TaskCompletionSource<(bool Approved, string? OptionId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingApprovals[reqId] = tcs;

        try
        {
            var cmdText = parameters.HasValue
                ? parameters.Value.GetStringOrDefault("command", method)
                : method;

            PublishEvent(new AcpPermissionRequestEvent(
                Provider: Provider,
                SessionId: SessionId,
                Timestamp: DateTimeOffset.UtcNow,
                RequestId: reqId,
                ToolName: method.Contains("commandExecution", StringComparison.Ordinal) ? "commandExecution" : "fileChange",
                Title: $"Approve {cmdText}?",
                Description: cmdText,
                RawInputJson: parameters?.GetRawText(),
                Options:
                [
                    new AcpPermissionOption("accept", "Accept", "allow_once"),
                    new AcpPermissionOption("decline", "Decline", "reject_once")
                ]));

            var (approved, _) = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return BuildDecisionPayload(approved);
        }
        finally
        {
            _pendingApprovals.TryRemove(reqId, out _);
        }
    }

    private static JsonElement BuildDecisionPayload(bool accepted)
    {
        return JsonElementBuilder.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("decision", accepted ? "accept" : "decline");
            w.WriteEndObject();
        });
    }
}
