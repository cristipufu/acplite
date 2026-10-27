using System.Threading.Channels;
using Acplite.Sdk.Protocol;

namespace Acplite.Sdk.Runtime;

/// <summary>
/// Abstraction for an external interactive session control channel (such as a local PTY pipe)
/// that can receive prompts, steering, permission decisions, and turn cancellation.
/// </summary>
public interface IAcpExternalSessionChannel : IAsyncDisposable
{
    int ProcessId { get; }
    IAsyncEnumerable<AcpEvent> ReadControlEventsAsync(CancellationToken cancellationToken = default);
    Task SendPromptAsync(string prompt, CancellationToken cancellationToken = default);
    Task SteerAsync(string prompt, CancellationToken cancellationToken = default);
    Task RespondPermissionAsync(
        string requestId,
        bool approved,
        string? selectedOptionId = null,
        CancellationToken cancellationToken = default);
    Task CancelTurnAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional bridge allowing <see cref="AcpliteRuntime"/> to discover, monitor, and attach to
/// externally hosted interactive terminal sessions (such as <c>Acplite.Pty</c> tabs) without
/// coupling <c>Acplite.Sdk</c> to <c>Acplite.Pty</c>.
/// </summary>
public interface IAcpExternalSessionBridge
{
    Task<bool> HasActiveSessionAsync(
        AgentProviderKind provider,
        string? sessionId,
        string? workingDirectory,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AcpEvent> MonitorSessionEventsAsync(
        AcpDiscoveredSession session,
        CancellationToken cancellationToken = default);

    Task<IAcpExternalSessionChannel?> TryConnectChannelAsync(
        AgentProviderKind provider,
        string sessionId,
        string workingDirectory,
        string? transcriptPath = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An <see cref="IAcpSessionHandle"/> that attaches to an already-running interactive agent
/// via an <see cref="IAcpExternalSessionChannel"/> while optionally live-tailing its <c>.jsonl</c> transcript.
/// </summary>
public sealed class PtyAttachedSessionHandle : IAcpSessionHandle
{
    private readonly IAcpExternalSessionChannel _channel;
    private readonly IAcpProviderAdapter _adapter;
    private readonly Channel<AcpEvent> _events = Channel.CreateUnbounded<AcpEvent>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });
    private readonly SemaphoreSlim _turnStateGate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly List<AcpQueuedPromptItem> _promptQueue = [];
    private readonly CancellationTokenSource _lifetimeCts = new();
    private volatile AcpQueuedPromptItem[] _promptQueueSnapshot = [];
    private readonly Task _controlPumpTask;
    private readonly Task? _transcriptPumpTask;
    private long _queueItemCounter;
    private int _isTurnActive;
    private int _isDisposing;

    internal Action<IAcpSessionHandle>? OnDisposed { get; set; }

    public PtyAttachedSessionHandle(
        AgentProviderKind provider,
        string sessionId,
        string workingDirectory,
        IAcpExternalSessionChannel channel,
        IAcpProviderAdapter adapter,
        AcpDiscoveredSession? discoveredSession = null,
        bool enableInternalTranscriptTail = true)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(adapter);

        Provider = provider;
        SessionId = sessionId;
        WorkingDirectory = workingDirectory;
        _channel = channel;
        _adapter = adapter;

        _controlPumpTask = PumpControlEventsAsync(_lifetimeCts.Token);
        if (enableInternalTranscriptTail && discoveredSession is not null)
        {
            _transcriptPumpTask = PumpTranscriptEventsAsync(discoveredSession, _lifetimeCts.Token);
        }
    }

    public AgentProviderKind Provider { get; }
    public string SessionId { get; }
    public string WorkingDirectory { get; }
    public bool SupportsLiveSteering => true;
    public bool IsTurnActive => Volatile.Read(ref _isTurnActive) != 0;
    public int PtyProcessId => _channel.ProcessId;

    public IReadOnlyList<AcpQueuedPromptItem> GetQueuedPrompts() => _promptQueueSnapshot;

    public IAsyncEnumerable<AcpEvent> ReadEventsAsync(CancellationToken cancellationToken = default)
        => _events.Reader.ReadAllAsync(cancellationToken);

    public async Task SendPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        Volatile.Write(ref _isTurnActive, 1);
        try
        {
            await _channel.SendPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _isTurnActive, 0);
            throw;
        }
    }

    public async Task<AcpQueuedPromptItem?> EnqueuePromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        AcpQueuedPromptItem? queuedItem = null;
        AcpQueuedPromptItem[]? snapshotToPublish = null;

        await _turnStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _isTurnActive) != 0)
            {
                queuedItem = new AcpQueuedPromptItem(
                    QueueItemId: $"q_{Interlocked.Increment(ref _queueItemCounter)}",
                    Prompt: prompt,
                    EnqueuedAt: DateTimeOffset.UtcNow);
                _promptQueue.Add(queuedItem);
                snapshotToPublish = _promptQueue.ToArray();
                _promptQueueSnapshot = snapshotToPublish;
            }
            else
            {
                Volatile.Write(ref _isTurnActive, 1);
            }
        }
        finally
        {
            _turnStateGate.Release();
        }

        if (queuedItem is not null)
        {
            PublishEvent(new AcpPromptQueueUpdatedEvent(Provider, SessionId, DateTimeOffset.UtcNow, snapshotToPublish!));
            return queuedItem;
        }

        await SendPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<bool> RemoveQueuedPromptAsync(string queueItemId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueItemId);

        AcpQueuedPromptItem[]? snapshotToPublish = null;
        var removed = false;

        await _turnStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < _promptQueue.Count; i++)
            {
                if (string.Equals(_promptQueue[i].QueueItemId, queueItemId, StringComparison.Ordinal))
                {
                    _promptQueue.RemoveAt(i);
                    removed = true;
                    snapshotToPublish = _promptQueue.ToArray();
                    _promptQueueSnapshot = snapshotToPublish;
                    break;
                }
            }
        }
        finally
        {
            _turnStateGate.Release();
        }

        if (removed)
        {
            PublishEvent(new AcpPromptQueueUpdatedEvent(Provider, SessionId, DateTimeOffset.UtcNow, snapshotToPublish!));
        }

        return removed;
    }

    public async Task ClearQueuedPromptsAsync(CancellationToken cancellationToken = default)
    {
        var hadItems = false;

        await _turnStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_promptQueue.Count > 0)
            {
                _promptQueue.Clear();
                _promptQueueSnapshot = [];
                hadItems = true;
            }
        }
        finally
        {
            _turnStateGate.Release();
        }

        if (hadItems)
        {
            PublishEvent(new AcpPromptQueueUpdatedEvent(Provider, SessionId, DateTimeOffset.UtcNow, []));
        }
    }

    public async Task InterruptAndSendPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        await CancelTurnAsync(cancellationToken).ConfigureAwait(false);
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        await SendPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SteerAsync(string steeringPrompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steeringPrompt);
        Volatile.Write(ref _isTurnActive, 1);
        await _channel.SteerAsync(steeringPrompt, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task RespondPermissionAsync(
        string requestId,
        bool approved,
        string? selectedOptionId = null,
        CancellationToken cancellationToken = default)
    {
        await _channel.RespondPermissionAsync(requestId, approved, selectedOptionId, cancellationToken).ConfigureAwait(false);
    }

    public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task CancelTurnAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _isTurnActive, 0);
        await _channel.CancelTurnAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpControlEventsAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await foreach (var ev in _channel.ReadControlEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                PublishEvent(ev);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch
        {
            // Channel closed
        }
    }

    private async Task PumpTranscriptEventsAsync(AcpDiscoveredSession session, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await foreach (var ev in _adapter.TailSessionTranscriptAsync(session, includeHistory: false, followLive: true, cancellationToken).ConfigureAwait(false))
            {
                PublishEvent(ev);
                if (ev is AcpTurnCompletedEvent completed)
                {
                    await CompleteTurnAndDrainQueueAsync(completed.StopReason, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch
        {
            // Ignore transcript tail errors
        }
    }

    private async Task CompleteTurnAndDrainQueueAsync(string stopReason, CancellationToken cancellationToken)
    {
        AcpQueuedPromptItem? nextItem = null;
        AcpQueuedPromptItem[]? snapshotToPublish = null;

        await _turnStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!AcpStopReasons.IsFailureOrCancellation(stopReason) && _promptQueue.Count > 0)
            {
                nextItem = _promptQueue[0];
                _promptQueue.RemoveAt(0);
                snapshotToPublish = _promptQueue.ToArray();
                _promptQueueSnapshot = snapshotToPublish;
                Volatile.Write(ref _isTurnActive, 1);
            }
            else
            {
                Volatile.Write(ref _isTurnActive, 0);
            }
        }
        finally
        {
            _turnStateGate.Release();
        }

        if (nextItem is not null)
        {
            PublishEvent(new AcpPromptQueueUpdatedEvent(Provider, SessionId, DateTimeOffset.UtcNow, snapshotToPublish!));
            await _channel.SendPromptAsync(nextItem.Prompt, cancellationToken).ConfigureAwait(false);
        }
    }

    private void PublishEvent(AcpEvent ev)
    {
        if (Volatile.Read(ref _isDisposing) == 0)
        {
            _events.Writer.TryWrite(ev);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _isDisposing) != 0)
        {
            return;
        }

        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _isDisposing, 1) != 0)
            {
                return;
            }

            await _lifetimeCts.CancelAsync().ConfigureAwait(false);
            _events.Writer.TryComplete();

            await _channel.DisposeAsync().ConfigureAwait(false);

            try { await _controlPumpTask.WaitAsync(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false); } catch { }
            if (_transcriptPumpTask is not null)
            {
                try { await _transcriptPumpTask.WaitAsync(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false); } catch { }
            }

            _lifetimeCts.Dispose();
            OnDisposed?.Invoke(this);
        }
        finally
        {
            _disposeGate.Release();
        }
    }
}
