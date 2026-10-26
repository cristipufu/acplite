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
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Process? _process;
    private Task? _stderrPumpTask;
    private Task? _exitMonitorTask;
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
    protected CancellationToken LifetimeToken => _lifetimeCts.Token;

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
        if (Volatile.Read(ref _isDisposing) == 0)
        {
            _events.Writer.TryWrite(acpEvent);
        }
    }

    protected void PublishClassifiedError(string? message, string? rawCode = null, int? rpcCode = null)
    {
        var diagnostic = AppendStderrTail(message);
        var classification = ProviderInterruptionClassifier.Classify(diagnostic, rawCode, rpcCode);
        PublishEvent(new AcpInterruptionEvent(Provider, SessionId, DateTimeOffset.UtcNow, classification));
    }

    public abstract Task InitializeAsync(CancellationToken cancellationToken = default);
    public abstract Task SendPromptAsync(string prompt, CancellationToken cancellationToken = default);
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
