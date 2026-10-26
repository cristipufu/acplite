using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;

namespace Acplite.Sdk.Adapters;

internal sealed class NativeAcpSessionHandle : AcpSessionHandleBase
{
    private readonly bool _preferXaiExtensions;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(bool Approved, string? OptionId)>> _pendingPermissions = new(StringComparer.Ordinal);
    private Task? _activePromptTask;

    public NativeAcpSessionHandle(
        AgentProviderKind provider,
        AcpSessionConfig config,
        Stream agentOutputStream,
        Stream agentInputStream,
        Process? process,
        bool preferXaiExtensions)
        : base(provider, config, agentOutputStream, agentInputStream, process)
    {
        _preferXaiExtensions = preferXaiExtensions;
        Transport.OnNotificationAsync = HandleNotificationAsync;
        Transport.OnRequestAsync = HandleServerRequestAsync;
    }

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StartBackgroundMonitors();

        var initParams = JsonElementBuilder.Build(static w =>
        {
            w.WriteStartObject();
            w.WriteNumber("protocolVersion", 1);
            w.WritePropertyName("clientCapabilities");
            w.WriteStartObject();
            w.WriteEndObject();
            w.WritePropertyName("clientInfo");
            w.WriteStartObject();
            w.WriteString("name", "acplite");
            w.WriteString("version", "0.1.0");
            w.WriteEndObject();
            w.WriteEndObject();
        });

        await Transport.SendRequestAsync("initialize", initParams, cancellationToken).ConfigureAwait(false);

        if (Config.ResumeExisting && !string.IsNullOrWhiteSpace(Config.SessionId))
        {
            var loadParams = JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("sessionId", Config.SessionId);
                w.WriteString("cwd", Config.WorkingDirectory);
                w.WritePropertyName("mcpServers");
                w.WriteStartArray();
                w.WriteEndArray();
                w.WriteEndObject();
            });
            await Transport.SendRequestAsync("session/load", loadParams, cancellationToken).ConfigureAwait(false);
            SessionId = Config.SessionId!;
        }
        else
        {
            var newParams = JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("cwd", Config.WorkingDirectory);
                w.WritePropertyName("mcpServers");
                w.WriteStartArray();
                w.WriteEndArray();
                w.WriteEndObject();
            });
            var res = await Transport.SendRequestAsync("session/new", newParams, cancellationToken).ConfigureAwait(false);
            var assignedId = res.GetOptionalString("sessionId");
            if (!string.IsNullOrWhiteSpace(assignedId))
            {
                SessionId = assignedId!;
            }
        }

        PublishEvent(new AcpSessionStartedEvent(
            Provider,
            SessionId,
            DateTimeOffset.UtcNow,
            Config.Model,
            Config.WorkingDirectory));
    }

    public override Task SendPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var promptParams = JsonElementBuilder.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("sessionId", SessionId);
            w.WritePropertyName("prompt");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", prompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        });

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken, cancellationToken);
        _activePromptTask = ExecutePromptRpcAsync(promptParams, linkedCts);
        return Task.CompletedTask;
    }

    private async Task ExecutePromptRpcAsync(JsonElement promptParams, CancellationTokenSource linkedCts)
    {
        using (linkedCts)
        {
            try
            {
                var res = await Transport.SendRequestAsync("session/prompt", promptParams, linkedCts.Token).ConfigureAwait(false);
                var stopReason = res.GetStringOrDefault("stopReason", "end_turn");
                PublishEvent(new AcpTurnCompletedEvent(Provider, SessionId, DateTimeOffset.UtcNow, stopReason));
            }
            catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
            {
                // Session shutting down
            }
            catch (Exception ex)
            {
                PublishClassifiedError(ex.Message);
                PublishEvent(new AcpTurnCompletedEvent(Provider, SessionId, DateTimeOffset.UtcNow, "error"));
            }
        }
    }

    public override async Task<bool> SteerAsync(string steeringPrompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(steeringPrompt))
        {
            return false;
        }

        var method = _preferXaiExtensions ? "_x.ai/interject" : "_session/steering";
        try
        {
            var steerParams = JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("sessionId", SessionId);
                w.WriteString("text", steeringPrompt);
                w.WritePropertyName("prompt");
                w.WriteStartArray();
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", steeringPrompt);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            });
            await Transport.SendRequestAsync(method, steerParams, cancellationToken).ConfigureAwait(false);
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

        if (_pendingPermissions.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult((approved, selectedOptionId));
        }
        return Task.CompletedTask;
    }

    public override async Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var modelParams = JsonElementBuilder.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("sessionId", SessionId);
            w.WriteString("modelId", modelId);
            w.WriteEndObject();
        });
        await Transport.SendRequestAsync("session/set_model", modelParams, cancellationToken).ConfigureAwait(false);
    }

    public override async Task CancelTurnAsync(CancellationToken cancellationToken = default)
    {
        var cancelParams = JsonElementBuilder.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("sessionId", SessionId);
            w.WriteEndObject();
        });
        await Transport.SendNotificationAsync("session/cancel", cancelParams, cancellationToken).ConfigureAwait(false);
    }

    protected override void OnSessionFaulted(Exception exception)
    {
        foreach (var (key, _) in _pendingPermissions)
        {
            if (_pendingPermissions.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(exception);
            }
        }
    }

    protected override async ValueTask OnDisposeSessionResourcesAsync()
    {
        var promptTask = _activePromptTask;
        if (promptTask is not null)
        {
            try
            {
                await promptTask.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }
        }
    }

    private Task HandleNotificationAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (method is not ("session/update" or "_x.ai/session/update") ||
            !parameters.HasValue ||
            !parameters.Value.TryGetObject("update", out var update))
        {
            return Task.CompletedTask;
        }

        var now = DateTimeOffset.UtcNow;
        var updateKind = update.GetOptionalString("sessionUpdate");

        switch (updateKind)
        {
            case "agent_message_chunk":
            {
                if (update.TryGetObject("content", out var content))
                {
                    var text = content.GetOptionalString("text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        PublishEvent(new AcpAssistantTextDeltaEvent(Provider, SessionId, now, text));
                    }
                }
                break;
            }

            case "agent_thought_chunk":
            {
                if (update.TryGetObject("content", out var content))
                {
                    var text = content.GetOptionalString("text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        PublishEvent(new AcpThinkingDeltaEvent(Provider, SessionId, now, text));
                    }
                }
                break;
            }

            case "tool_call":
            case "tool_call_update":
            {
                var tcId = update.GetStringOrDefault("toolCallId", Guid.NewGuid().ToString("N"));
                var title = update.GetStringOrDefault("title", "tool");
                var kind = update.GetStringOrDefault("kind", "other");
                var status = update.GetStringOrDefault("status", "in_progress");
                PublishEvent(new AcpToolCallEvent(Provider, SessionId, now, tcId, title, title, kind, status));
                break;
            }
        }

        return Task.CompletedTask;
    }

    private async Task<JsonElement?> HandleServerRequestAsync(
        JsonElement idElement,
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        if (method != "session/request_permission")
        {
            return JsonElementBuilder.EmptyObject;
        }

        var reqId = idElement.ValueKind == JsonValueKind.String ? idElement.GetString()! : idElement.GetRawText();
        var options = new List<AcpPermissionOption>();
        var title = "Tool permission";

        if (parameters.HasValue)
        {
            var p = parameters.Value;
            if (p.TryGetObject("toolCall", out var tc))
            {
                title = tc.GetStringOrDefault("title", title);
            }

            if (p.TryGetArray("options", out var optsArr))
            {
                foreach (var opt in optsArr.EnumerateArray())
                {
                    var oid = opt.GetStringOrDefault("optionId", "allow");
                    var oname = opt.GetStringOrDefault("name", oid);
                    var okind = opt.GetStringOrDefault("kind", "allow_once");
                    options.Add(new AcpPermissionOption(oid, oname, okind));
                }
            }
        }

        if (options.Count == 0)
        {
            options.Add(new AcpPermissionOption("allow", "Allow", "allow_once"));
            options.Add(new AcpPermissionOption("deny", "Deny", "reject_once"));
        }

        if (Config.AutoApproveTools)
        {
            var allowOpt = options.FirstOrDefault(static o => o.Kind.StartsWith("allow", StringComparison.OrdinalIgnoreCase)) ?? options[0];
            return BuildPermissionOutcome(allowOpt.OptionId);
        }

        var tcs = new TaskCompletionSource<(bool Approved, string? OptionId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPermissions[reqId] = tcs;

        try
        {
            PublishEvent(new AcpPermissionRequestEvent(
                Provider: Provider,
                SessionId: SessionId,
                Timestamp: DateTimeOffset.UtcNow,
                RequestId: reqId,
                ToolName: title,
                Title: title,
                Description: null,
                RawInputJson: parameters?.GetRawText(),
                Options: options));

            var (approved, selectedOptionId) = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var chosen = selectedOptionId
                ?? (approved
                    ? (options.FirstOrDefault(static o => o.Kind.StartsWith("allow", StringComparison.OrdinalIgnoreCase))?.OptionId ?? options[0].OptionId)
                    : (options.FirstOrDefault(static o => o.Kind.StartsWith("reject", StringComparison.OrdinalIgnoreCase))?.OptionId ?? options[^1].OptionId));

            return BuildPermissionOutcome(chosen);
        }
        finally
        {
            _pendingPermissions.TryRemove(reqId, out _);
        }
    }

    private static JsonElement BuildPermissionOutcome(string optionId)
    {
        return JsonElementBuilder.Build(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("outcome");
            w.WriteStartObject();
            w.WriteString("outcome", "selected");
            w.WriteString("optionId", optionId);
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }
}
