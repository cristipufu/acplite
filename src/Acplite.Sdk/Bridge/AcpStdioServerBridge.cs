using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;
using Acplite.Sdk.Transport;

namespace Acplite.Sdk.Bridge;

/// <summary>
/// Exposes any <see cref="IAcpProviderAdapter"/> (<c>claude</c>, <c>codex</c>, <c>grok</c>, <c>agy</c>)
/// as a standards-compliant ACP JSON-RPC 2.0 server over <c>stdio</c> or arbitrary streams.
/// </summary>
public sealed class AcpStdioServerBridge : IAsyncDisposable
{
    private readonly IAcpProviderAdapter _adapter;
    private readonly AcpJsonRpcTransport _hostTransport;
    private readonly string? _binaryPathOverride;
    private readonly ConcurrentDictionary<string, BridgeSessionContext> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly TaskCompletionSource _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _isDisposed;

    public AcpStdioServerBridge(
        IAcpProviderAdapter adapter,
        Stream hostInputStream,
        Stream hostOutputStream,
        string? binaryPathOverride = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(hostInputStream);
        ArgumentNullException.ThrowIfNull(hostOutputStream);

        _adapter = adapter;
        _binaryPathOverride = binaryPathOverride;
        _hostTransport = new AcpJsonRpcTransport(hostInputStream, hostOutputStream)
        {
            OnRequestAsync = HandleHostRequestAsync,
            OnNotificationAsync = HandleHostNotificationAsync,
            OnTransportError = ex => _completionTcs.TrySetException(ex),
            OnInputStreamClosed = () => _completionTcs.TrySetResult()
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _hostTransport.Start();
        using var reg = cancellationToken.Register(() => _completionTcs.TrySetCanceled(cancellationToken));
        await _completionTcs.Task.ConfigureAwait(false);
    }

    public void StartBackground()
    {
        _hostTransport.Start();
    }

    private async Task<JsonElement?> HandleHostRequestAsync(
        JsonElement idElement,
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
            {
                return JsonElementBuilder.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("protocolVersion", 1);
                    w.WritePropertyName("agentCapabilities");
                    w.WriteStartObject();
                    w.WriteBoolean("loadSession", true);
                    w.WritePropertyName("promptCapabilities");
                    w.WriteStartObject();
                    w.WriteBoolean("image", true);
                    w.WriteBoolean("embeddedContext", true);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WritePropertyName("agentInfo");
                    w.WriteStartObject();
                    w.WriteString("name", $"acplite ({_adapter.ShortId})");
                    w.WriteString("title", _adapter.DisplayName);
                    w.WriteString("version", "0.1.1");
                    w.WriteEndObject();
                    w.WriteEndObject();
                });
            }

            case "session/new":
            case "session/load":
            {
                var cwd = parameters.HasValue
                    ? parameters.Value.GetStringOrDefault("cwd", Environment.CurrentDirectory)
                    : Environment.CurrentDirectory;

                var requestedSessionId = parameters.HasValue
                    ? parameters.Value.GetOptionalString("sessionId")
                    : null;

                var handle = await _adapter.StartSessionAsync(new AcpSessionConfig
                {
                    Provider = _adapter.Provider,
                    WorkingDirectory = cwd,
                    SessionId = requestedSessionId,
                    ResumeExisting = method == "session/load" && !string.IsNullOrWhiteSpace(requestedSessionId),
                    BinaryPathOverride = _binaryPathOverride
                }, cancellationToken).ConfigureAwait(false);

                var ctx = new BridgeSessionContext(handle, _hostTransport);
                _sessions[handle.SessionId] = ctx;
                ctx.StartPump(cancellationToken);

                return JsonElementBuilder.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("sessionId", handle.SessionId);
                    w.WriteEndObject();
                });
            }

            case "session/prompt":
            {
                var sessionId = ExtractSessionId(parameters);
                if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var ctx))
                {
                    throw new InvalidOperationException($"Unknown ACP session '{sessionId}'.");
                }

                var promptText = ExtractPromptText(parameters);
                var stopReason = await ctx.ExecutePromptTurnAsync(promptText, cancellationToken).ConfigureAwait(false);

                return JsonElementBuilder.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("stopReason", stopReason);
                    w.WriteEndObject();
                });
            }

            case "_session/steering":
            case "_x.ai/interject":
            {
                var sessionId = ExtractSessionId(parameters);
                if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var ctx))
                {
                    throw new InvalidOperationException($"Unknown ACP session '{sessionId}'.");
                }

                var text = ExtractPromptText(parameters);
                var accepted = await ctx.Handle.SteerAsync(text, cancellationToken).ConfigureAwait(false);
                return JsonElementBuilder.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteBoolean("accepted", accepted);
                    w.WriteEndObject();
                });
            }

            case "_session/queue":
            case "session/queue":
            {
                var sessionId = ExtractSessionId(parameters);
                if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var ctx))
                {
                    throw new InvalidOperationException($"Unknown ACP session '{sessionId}'.");
                }

                var text = ExtractPromptText(parameters);
                var queuedItem = await ctx.Handle.EnqueuePromptAsync(text, cancellationToken).ConfigureAwait(false);
                var queueCount = ctx.Handle.GetQueuedPrompts().Count;
                return JsonElementBuilder.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteBoolean("queued", queuedItem is not null);
                    if (queuedItem is not null)
                    {
                        w.WriteString("queueItemId", queuedItem.QueueItemId);
                    }
                    w.WriteNumber("queueLength", queueCount);
                    w.WriteEndObject();
                });
            }

            case "_session/queue/remove":
            case "session/queue_remove":
            {
                var sessionId = ExtractSessionId(parameters);
                if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var ctx))
                {
                    throw new InvalidOperationException($"Unknown ACP session '{sessionId}'.");
                }

                var queueItemId = parameters.HasValue ? parameters.Value.GetStringOrDefault("queueItemId") : string.Empty;
                var removed = !string.IsNullOrWhiteSpace(queueItemId) &&
                              await ctx.Handle.RemoveQueuedPromptAsync(queueItemId, cancellationToken).ConfigureAwait(false);
                return JsonElementBuilder.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteBoolean("removed", removed);
                    w.WriteNumber("queueLength", ctx.Handle.GetQueuedPrompts().Count);
                    w.WriteEndObject();
                });
            }

            case "_session/queue/clear":
            case "session/queue_clear":
            {
                var sessionId = ExtractSessionId(parameters);
                if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var ctx))
                {
                    throw new InvalidOperationException($"Unknown ACP session '{sessionId}'.");
                }

                await ctx.Handle.ClearQueuedPromptsAsync(cancellationToken).ConfigureAwait(false);
                return JsonElementBuilder.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteBoolean("cleared", true);
                    w.WriteEndObject();
                });
            }

            case "_session/interrupt_and_prompt":
            case "session/interrupt_and_prompt":
            {
                var sessionId = ExtractSessionId(parameters);
                if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var ctx))
                {
                    throw new InvalidOperationException($"Unknown ACP session '{sessionId}'.");
                }

                var text = ExtractPromptText(parameters);
                await ctx.Handle.InterruptAndSendPromptAsync(text, cancellationToken).ConfigureAwait(false);
                return JsonElementBuilder.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteBoolean("accepted", true);
                    w.WriteEndObject();
                });
            }

            case "session/set_model":
            {
                var sessionId = ExtractSessionId(parameters);
                var modelId = parameters.HasValue ? parameters.Value.GetStringOrDefault("modelId") : string.Empty;

                if (!string.IsNullOrEmpty(sessionId) &&
                    !string.IsNullOrEmpty(modelId) &&
                    _sessions.TryGetValue(sessionId, out var ctx))
                {
                    await ctx.Handle.SetModelAsync(modelId, cancellationToken).ConfigureAwait(false);
                }

                return JsonElementBuilder.EmptyObject;
            }

            default:
                return JsonElementBuilder.EmptyObject;
        }
    }

    private async Task HandleHostNotificationAsync(
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        if (method == "session/cancel")
        {
            var sessionId = ExtractSessionId(parameters);
            if (!string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var ctx))
            {
                await ctx.Handle.CancelTurnAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string ExtractSessionId(JsonElement? parameters)
        => parameters.HasValue ? parameters.Value.GetStringOrDefault("sessionId") : string.Empty;

    private static string ExtractPromptText(JsonElement? parameters)
    {
        if (!parameters.HasValue || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var p = parameters.Value;
        var directText = p.GetOptionalString("text");
        if (!string.IsNullOrEmpty(directText))
        {
            return directText;
        }

        if (p.TryGetArray("prompt", out var promptArr))
        {
            var sb = new StringBuilder();
            foreach (var block in promptArr.EnumerateArray())
            {
                var txt = block.GetOptionalString("text");
                if (!string.IsNullOrEmpty(txt))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(txt);
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            _completionTcs.TrySetResult();

            foreach (var (_, ctx) in _sessions)
            {
                await ctx.DisposeAsync().ConfigureAwait(false);
            }
            _sessions.Clear();

            await _hostTransport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private sealed class BridgeSessionContext : IAsyncDisposable
    {
        private readonly AcpJsonRpcTransport _hostTransport;
        private readonly SemaphoreSlim _turnGate = new(1, 1);
        private readonly ConcurrentDictionary<long, Task> _inFlightPermissions = new();
        private TaskCompletionSource<string>? _activeTurnTcs;
        private CancellationTokenSource? _pumpCts;
        private Task? _pumpTask;
        private long _permissionCounter;
        private int _isDisposed;

        public IAcpSessionHandle Handle { get; }

        public BridgeSessionContext(IAcpSessionHandle handle, AcpJsonRpcTransport hostTransport)
        {
            Handle = handle;
            _hostTransport = hostTransport;
        }

        public void StartPump(CancellationToken parentToken)
        {
            _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _pumpTask = Task.Run(() => PumpEventsAsync(_pumpCts.Token), _pumpCts.Token);
        }

        public async Task<string> ExecutePromptTurnAsync(string promptText, CancellationToken cancellationToken)
        {
            await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var turnTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _activeTurnTcs, turnTcs);
                try
                {
                    await Handle.SendPromptAsync(promptText, cancellationToken).ConfigureAwait(false);
                    return await turnTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _activeTurnTcs, null);
                }
            }
            finally
            {
                _turnGate.Release();
            }
        }

        private async Task PumpEventsAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var ev in Handle.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
                {
                    switch (ev)
                    {
                        case AcpAssistantTextDeltaEvent textDelta:
                            await SendTextChunkUpdateAsync("agent_message_chunk", textDelta.Delta, cancellationToken).ConfigureAwait(false);
                            break;

                        case AcpThinkingDeltaEvent thoughtDelta:
                            await SendTextChunkUpdateAsync("agent_thought_chunk", thoughtDelta.Delta, cancellationToken).ConfigureAwait(false);
                            break;

                        case AcpToolCallEvent toolCall:
                            await SendToolCallUpdateAsync(toolCall, cancellationToken).ConfigureAwait(false);
                            break;

                        case AcpPermissionRequestEvent perm:
                        {
                            var permId = Interlocked.Increment(ref _permissionCounter);
                            var task = ForwardPermissionRequestAsync(permId, perm, cancellationToken);
                            _inFlightPermissions[permId] = task;
                            break;
                        }

                        case AcpTurnCompletedEvent completed:
                            Volatile.Read(ref _activeTurnTcs)?.TrySetResult(completed.StopReason);
                            break;

                        case AcpPromptQueueUpdatedEvent queueUpdated:
                            await SendPromptQueueUpdateAsync(queueUpdated, cancellationToken).ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown
            }
            finally
            {
                Volatile.Read(ref _activeTurnTcs)?.TrySetResult("end_turn");
            }
        }

        private Task SendPromptQueueUpdateAsync(AcpPromptQueueUpdatedEvent queueUpdated, CancellationToken cancellationToken)
        {
            var updateParams = JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("sessionId", Handle.SessionId);
                w.WritePropertyName("update");
                w.WriteStartObject();
                w.WriteString("sessionUpdate", "prompt_queue_update");
                w.WritePropertyName("queuedPrompts");
                w.WriteStartArray();
                foreach (var item in queueUpdated.QueuedPrompts)
                {
                    w.WriteStartObject();
                    w.WriteString("queueItemId", item.QueueItemId);
                    w.WriteString("prompt", item.Prompt);
                    w.WriteString("enqueuedAt", item.EnqueuedAt.ToString("O"));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteEndObject();
            });
            return _hostTransport.SendNotificationAsync("session/update", updateParams, cancellationToken);
        }

        private Task SendTextChunkUpdateAsync(string sessionUpdateKind, string text, CancellationToken cancellationToken)
        {
            var updateParams = JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("sessionId", Handle.SessionId);
                w.WritePropertyName("update");
                w.WriteStartObject();
                w.WriteString("sessionUpdate", sessionUpdateKind);
                w.WritePropertyName("content");
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", text);
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
            });
            return _hostTransport.SendNotificationAsync("session/update", updateParams, cancellationToken);
        }

        private Task SendToolCallUpdateAsync(AcpToolCallEvent toolCall, CancellationToken cancellationToken)
        {
            var updateParams = JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("sessionId", Handle.SessionId);
                w.WritePropertyName("update");
                w.WriteStartObject();
                w.WriteString("sessionUpdate", toolCall.Status == "in_progress" ? "tool_call" : "tool_call_update");
                w.WriteString("toolCallId", toolCall.ToolCallId);
                w.WriteString("title", toolCall.Title);
                w.WriteString("kind", toolCall.Kind);
                w.WriteString("status", toolCall.Status);
                w.WriteEndObject();
                w.WriteEndObject();
            });
            return _hostTransport.SendNotificationAsync("session/update", updateParams, cancellationToken);
        }

        private async Task ForwardPermissionRequestAsync(
            long trackingId,
            AcpPermissionRequestEvent perm,
            CancellationToken cancellationToken)
        {
            try
            {
                var permParams = JsonElementBuilder.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("sessionId", Handle.SessionId);
                    w.WritePropertyName("toolCall");
                    w.WriteStartObject();
                    w.WriteString("toolCallId", perm.RequestId);
                    w.WriteString("title", perm.Title);
                    w.WriteEndObject();
                    w.WritePropertyName("options");
                    w.WriteStartArray();
                    foreach (var opt in perm.Options)
                    {
                        w.WriteStartObject();
                        w.WriteString("optionId", opt.OptionId);
                        w.WriteString("name", opt.Name);
                        w.WriteString("kind", opt.Kind);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                });

                var hostDecision = await _hostTransport.SendRequestAsync("session/request_permission", permParams, cancellationToken).ConfigureAwait(false);
                string? selectedOpt = null;
                if (hostDecision.TryGetObject("outcome", out var outcome))
                {
                    selectedOpt = outcome.GetOptionalString("optionId");
                }

                var isApproved = selectedOpt is null ||
                    (!selectedOpt.Contains("deny", StringComparison.OrdinalIgnoreCase) &&
                     !selectedOpt.Contains("reject", StringComparison.OrdinalIgnoreCase) &&
                     !selectedOpt.Contains("decline", StringComparison.OrdinalIgnoreCase));

                await Handle.RespondPermissionAsync(perm.RequestId, isApproved, selectedOpt, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await Handle.RespondPermissionAsync(perm.RequestId, approved: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Session may already be disposed
                }
            }
            finally
            {
                _inFlightPermissions.TryRemove(trackingId, out _);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            Volatile.Read(ref _activeTurnTcs)?.TrySetCanceled();

            if (_pumpCts is not null)
            {
                await _pumpCts.CancelAsync().ConfigureAwait(false);
            }

            var pumpFinished = true;
            if (_pumpTask is not null)
            {
                try
                {
                    await _pumpTask.WaitAsync(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
                }
                catch
                {
                    pumpFinished = _pumpTask.IsCompleted;
                }
            }

            foreach (var (_, task) in _inFlightPermissions)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore timeout/cancellation during teardown
                }
            }
            _inFlightPermissions.Clear();

            if (pumpFinished)
            {
                _pumpCts?.Dispose();
            }

            await Handle.DisposeAsync().ConfigureAwait(false);
        }
    }
}
