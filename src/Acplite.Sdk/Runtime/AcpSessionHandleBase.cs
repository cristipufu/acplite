using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Transport;

namespace Acplite.Sdk.Runtime;

/// <summary>
/// Base abstraction for active ACP session handles. Coordinates event streaming, continuous
/// <c>stderr</c> draining (preventing OS pipe buffer deadlocks), unexpected child-process crash
/// detection, and idempotent <see cref="SemaphoreSlim"/>-gated async disposal.
/// </summary>
public abstract class AcpSessionHandleBase : IAcpSessionHandle
{
    private const int MaxBufferedStderrLines = 24;

    private readonly Channel<AcpEvent> _events = Channel.CreateUnbounded<AcpEvent>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    private readonly ConcurrentQueue<string> _recentStderrLines = new();
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly SemaphoreSlim _turnStateGate = new(1, 1);
    private readonly List<AcpQueuedPromptItem> _promptQueue = [];
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Process? _process;
    private volatile AcpQueuedPromptItem[] _promptQueueSnapshot = [];
    private TaskCompletionSource<string>? _activeTurnCompletionTcs;
    private Task? _stderrPumpTask;
    private Task? _exitMonitorTask;
    private long _activeTurnGeneration;
    private long _queueItemCounter;
    private int _isTurnActive;
    private int _suppressAutoDrain;
    private int _isDisposing;

    protected AcpSessionConfig Config { get; }
    protected AcpJsonRpcTransport Transport { get; }
    internal Action<IAcpSessionHandle>? OnDisposed { get; set; }

    protected AcpSessionHandleBase(
        AgentProviderKind provider,
        AcpSessionConfig config,
        Stream agentOutputStream,
        Stream agentInputStream,
        Process? process)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(agentOutputStream);
        ArgumentNullException.ThrowIfNull(agentInputStream);

        Provider = provider;
        Config = config;
        _process = process;
        SessionId = !string.IsNullOrWhiteSpace(config.SessionId)
            ? config.SessionId!
            : Guid.NewGuid().ToString("N");

        Transport = new AcpJsonRpcTransport(agentOutputStream, agentInputStream)
        {
            OnTransportError = HandleTransportException
        };
    }

    public AgentProviderKind Provider { get; }
    public string SessionId { get; protected set; }
    public string WorkingDirectory => Config.WorkingDirectory;
    public virtual bool SupportsLiveSteering => true;
    public bool IsTurnActive => Volatile.Read(ref _isTurnActive) != 0;
    protected CancellationToken LifetimeToken => _lifetimeCts.Token;

    public IReadOnlyList<AcpQueuedPromptItem> GetQueuedPrompts() => _promptQueueSnapshot;

    protected void StartBackgroundMonitors()
    {
        Transport.Start();

        if (_process is not null)
        {
            _stderrPumpTask = Task.Run(() => DrainStandardErrorAsync(_process, _lifetimeCts.Token));
            _exitMonitorTask = Task.Run(() => MonitorUnexpectedProcessExitAsync(_process, _lifetimeCts.Token));
        }
    }

    public IAsyncEnumerable<AcpEvent> ReadEventsAsync(CancellationToken cancellationToken = default)
        => _events.Reader.ReadAllAsync(cancellationToken);

    protected void PublishEvent(AcpEvent acpEvent)
    {
        if (Volatile.Read(ref _isDisposing) != 0)
        {
            return;
        }

        _events.Writer.TryWrite(acpEvent);

        if (acpEvent is AcpTurnCompletedEvent completed)
        {
            var completedGen = Volatile.Read(ref _activeTurnGeneration);
            Volatile.Read(ref _activeTurnCompletionTcs)?.TrySetResult(completed.StopReason);
            _ = CompleteTurnAndDrainQueueAsync(completed.StopReason, completedGen);
        }
    }

    protected void PublishClassifiedError(string? message, string? rawCode = null, int? rpcCode = null)
    {
        var diagnostic = AppendStderrTail(message);
        var classification = ProviderInterruptionClassifier.Classify(diagnostic, rawCode, rpcCode);
        PublishEvent(new AcpInterruptionEvent(Provider, SessionId, DateTimeOffset.UtcNow, classification));
    }

    public abstract Task InitializeAsync(CancellationToken cancellationToken = default);

    public async Task SendPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        await _turnStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _isTurnActive, 1);
            Interlocked.Increment(ref _activeTurnGeneration);
            Volatile.Write(
                ref _activeTurnCompletionTcs,
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        finally
        {
            _turnStateGate.Release();
        }

        try
        {
            await SendPromptCoreAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _isTurnActive, 0);
            Volatile.Read(ref _activeTurnCompletionTcs)?.TrySetResult("error");
            throw;
        }
    }

    protected abstract Task SendPromptCoreAsync(string prompt, CancellationToken cancellationToken = default);

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
                Interlocked.Increment(ref _activeTurnGeneration);
                Volatile.Write(
                    ref _activeTurnCompletionTcs,
                    new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
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

        try
        {
            await SendPromptCoreAsync(prompt, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch
        {
            Volatile.Write(ref _isTurnActive, 0);
            Volatile.Read(ref _activeTurnCompletionTcs)?.TrySetResult("error");
            throw;
        }
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

        Interlocked.Exchange(ref _suppressAutoDrain, 1);
        try
        {
            if (IsTurnActive)
            {
                var activeTcs = Volatile.Read(ref _activeTurnCompletionTcs);
                await CancelTurnAsync(cancellationToken).ConfigureAwait(false);
                if (activeTcs is not null)
                {
                    try
                    {
                        await activeTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Underlying agent CLI may not emit a turn-end frame on interrupt; proceed with the new turn
                    }
                }
            }

            await SendPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _suppressAutoDrain, 0);
        }
    }

    private async Task CompleteTurnAndDrainQueueAsync(string stopReason, long completedGen)
    {
        AcpQueuedPromptItem? nextItem = null;
        AcpQueuedPromptItem[]? snapshotToPublish = null;

        try
        {
            await _turnStateGate.WaitAsync(_lifetimeCts.Token).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _activeTurnGeneration) != completedGen)
                {
                    return;
                }

                var canAutoDrain = Volatile.Read(ref _suppressAutoDrain) == 0 &&
                                   ShouldAutoDrainQueue(stopReason);

                if (canAutoDrain && _promptQueue.Count > 0)
                {
                    nextItem = _promptQueue[0];
                    _promptQueue.RemoveAt(0);
                    snapshotToPublish = _promptQueue.ToArray();
                    _promptQueueSnapshot = snapshotToPublish;
                    Volatile.Write(ref _isTurnActive, 1);
                    Interlocked.Increment(ref _activeTurnGeneration);
                    Volatile.Write(
                        ref _activeTurnCompletionTcs,
                        new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
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
                await SendPromptCoreAsync(nextItem.Prompt, _lifetimeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _isTurnActive, 0);
            PublishClassifiedError(ex.Message, "queue_drain_error");
            PublishEvent(new AcpTurnCompletedEvent(Provider, SessionId, DateTimeOffset.UtcNow, "error"));
        }
    }

    private static bool ShouldAutoDrainQueue(string? stopReason)
    {
        if (string.IsNullOrWhiteSpace(stopReason))
        {
            return true;
        }

        return !stopReason.Equals("error", StringComparison.OrdinalIgnoreCase) &&
               !stopReason.Equals("cancelled", StringComparison.OrdinalIgnoreCase) &&
               !stopReason.Equals("canceled", StringComparison.OrdinalIgnoreCase) &&
               !stopReason.Equals("interrupted", StringComparison.OrdinalIgnoreCase);
    }

    public abstract Task<bool> SteerAsync(string steeringPrompt, CancellationToken cancellationToken = default);
    public abstract Task RespondPermissionAsync(
        string requestId,
        bool approved,
        string? selectedOptionId = null,
        CancellationToken cancellationToken = default);
    public abstract Task SetModelAsync(string modelId, CancellationToken cancellationToken = default);
    public abstract Task CancelTurnAsync(CancellationToken cancellationToken = default);

    protected virtual void OnSessionFaulted(Exception exception)
    {
    }

    protected virtual ValueTask OnDisposeSessionResourcesAsync()
        => ValueTask.CompletedTask;

    private void HandleTransportException(Exception exception)
    {
        if (Volatile.Read(ref _isDisposing) != 0)
        {
            return;
        }

        PublishClassifiedError(exception.Message, "transport_error");
        PublishEvent(new AcpTurnCompletedEvent(Provider, SessionId, DateTimeOffset.UtcNow, "error"));
        OnSessionFaulted(exception);
    }

    private async Task DrainStandardErrorAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            var reader = process.StandardError;
            string? line;
            while (!cancellationToken.IsCancellationRequested &&
                   (line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                _recentStderrLines.Enqueue(trimmed);
                while (_recentStderrLines.Count > MaxBufferedStderrLines)
                {
                    _recentStderrLines.TryDequeue(out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch
        {
            // Stream closed on process exit
        }
    }

    private async Task MonitorUnexpectedProcessExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _isDisposing) != 0)
            {
                return;
            }

            if (_stderrPumpTask is not null)
            {
                try
                {
                    await _stderrPumpTask.WaitAsync(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore timeout
                }
            }

            var exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                var crashMessage = AppendStderrTail($"Agent process exited unexpectedly with code {exitCode}.");
                var classification = ProviderInterruptionClassifier.Classify(crashMessage, "process_crashed");
                PublishEvent(new AcpInterruptionEvent(Provider, SessionId, DateTimeOffset.UtcNow, classification));
                PublishEvent(new AcpTurnCompletedEvent(Provider, SessionId, DateTimeOffset.UtcNow, "error"));
                OnSessionFaulted(new InvalidOperationException(crashMessage));
            }
            else
            {
                // Give stdout reader a brief window to flush final JSON frames before completing the event stream
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            _events.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _isDisposing) == 0)
            {
                OnSessionFaulted(ex);
            }
        }
    }

    private string AppendStderrTail(string? primaryMessage)
    {
        var stderrSnapshot = _recentStderrLines.ToArray();
        if (stderrSnapshot.Length == 0)
        {
            return primaryMessage ?? string.Empty;
        }

        var tail = string.Join(" | ", stderrSnapshot.TakeLast(4));
        return string.IsNullOrWhiteSpace(primaryMessage)
            ? tail
            : $"{primaryMessage} ({tail})";
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
            OnSessionFaulted(new ObjectDisposedException(GetType().Name));
            _events.Writer.TryComplete();

            await OnDisposeSessionResourcesAsync().ConfigureAwait(false);
            await Transport.DisposeAsync().ConfigureAwait(false);
            await ProcessTreeStopper.StopProcessTreeAsync(_process).ConfigureAwait(false);

            var stderrDone = await WaitBackgroundTaskQuietlyAsync(_stderrPumpTask).ConfigureAwait(false);
            var exitDone = await WaitBackgroundTaskQuietlyAsync(_exitMonitorTask).ConfigureAwait(false);

            if (stderrDone && exitDone)
            {
                _lifetimeCts.Dispose();
            }

            OnDisposed?.Invoke(this);
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private static async Task<bool> WaitBackgroundTaskQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return true;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return task.IsCompleted;
        }
    }
}
