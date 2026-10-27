using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Acplite.Pty;

/// <summary>
/// Client for communicating with a live <see cref="PtyControlServer"/> over a local
/// current-user-only named pipe / Unix domain socket.
/// </summary>
public sealed class PtyControlClient : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PtyControlEvent>> _pendingRequests = new(StringComparer.Ordinal);
    private readonly Channel<PtyControlEvent> _events = Channel.CreateUnbounded<PtyControlEvent>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = true
    });
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly TaskCompletionSource _helloReceivedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _readLoopTask;
    private PtySessionMetadata? _latestMetadata;
    private PtyPendingPermission? _activePermission;
    private long _requestCounter;
    private int _isDisposing;

    private PtyControlClient(string pipeName, NamedPipeClientStream pipe)
    {
        PipeName = pipeName;
        _pipe = pipe;
    }

    public string PipeName { get; }
    public bool IsConnected => _pipe.IsConnected && Volatile.Read(ref _isDisposing) == 0;
    public PtySessionMetadata? LatestMetadata => Volatile.Read(ref _latestMetadata);
    public PtyPendingPermission? ActivePermission => Volatile.Read(ref _activePermission);

    public static async Task<PtyControlClient> ConnectAsync(
        string pipeName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        PtyControlClient? client = null;
        try
        {
            var connectTimeout = timeout ?? TimeSpan.FromSeconds(3);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(connectTimeout);

            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            client = new PtyControlClient(pipeName, pipe);
            client.StartReadLoop();
            await client._helloReceivedTcs.Task.WaitAsync(connectCts.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    public static async Task<PtyControlClient?> TryConnectToSessionAsync(
        string providerShortId,
        string? sessionId,
        string? workingDirectory,
        string? transcriptPath = null,
        string? registryDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var matched = await PtySessionRegistry.FindMatchingSessionAsync(
            providerShortId,
            sessionId,
            workingDirectory,
            registryDirectory,
            cancellationToken).ConfigureAwait(false);

        if (matched is null)
        {
            return null;
        }

        PtyControlClient? client = null;
        try
        {
            client = await ConnectAsync(matched.PipeName, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(matched.SessionId) && !string.IsNullOrWhiteSpace(sessionId))
            {
                try
                {
                    await client.BindSessionAsync(sessionId, transcriptPath, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore bind failure
                }
            }

            return client;
        }
        catch
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            return null;
        }
    }

    public IAsyncEnumerable<PtyControlEvent> ReadEventsAsync(CancellationToken cancellationToken = default)
        => _events.Reader.ReadAllAsync(cancellationToken);

    public async Task<PtyControlEvent> GetStatusAsync(CancellationToken cancellationToken = default)
        => await SendRequestAsync(new PtyControlRequest
        {
            Id = NextRequestId(),
            Command = PtyControlCommands.GetStatus
        }, cancellationToken).ConfigureAwait(false);

    public async Task SendPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        _ = await SendRequestAsync(new PtyControlRequest
        {
            Id = NextRequestId(),
            Command = PtyControlCommands.SendPrompt,
            Text = prompt
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SteerAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        _ = await SendRequestAsync(new PtyControlRequest
        {
            Id = NextRequestId(),
            Command = PtyControlCommands.Steer,
            Text = prompt
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RespondPermissionAsync(
        string requestId,
        bool approved,
        string? selectedOptionId = null,
        CancellationToken cancellationToken = default)
    {
        _ = await SendRequestAsync(new PtyControlRequest
        {
            Id = NextRequestId(),
            Command = PtyControlCommands.RespondPermission,
            RequestId = requestId,
            Approved = approved,
            SelectedOptionId = selectedOptionId
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelTurnAsync(CancellationToken cancellationToken = default)
    {
        _ = await SendRequestAsync(new PtyControlRequest
        {
            Id = NextRequestId(),
            Command = PtyControlCommands.CancelTurn
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task BindSessionAsync(
        string? sessionId,
        string? transcriptPath,
        CancellationToken cancellationToken = default)
    {
        _ = await SendRequestAsync(new PtyControlRequest
        {
            Id = NextRequestId(),
            Command = PtyControlCommands.BindSession,
            SessionId = sessionId,
            TranscriptPath = transcriptPath
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PtyControlEvent> SendRequestAsync(
        PtyControlRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposing) != 0, this);

        var tcs = new TaskCompletionSource<PtyControlEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[request.Id] = tcs;

        try
        {
            var line = JsonSerializer.Serialize(request, PtyJsonContext.Default.PtyControlRequest) + "\n";
            var bytes = Utf8NoBom.GetBytes(line);

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (response.Type == PtyControlEventTypes.Error || response.Success == false)
            {
                throw new InvalidOperationException(response.Message ?? "PTY control request failed.");
            }

            return response;
        }
        finally
        {
            _pendingRequests.TryRemove(request.Id, out _);
        }
    }

    private void StartReadLoop()
    {
        _readLoopTask = ReadLoopAsync(_lifetimeCts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            using var reader = new StreamReader(_pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            while (!cancellationToken.IsCancellationRequested && _pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                PtyControlEvent? ev;
                try
                {
                    ev = JsonSerializer.Deserialize(line, PtyJsonContext.Default.PtyControlEvent);
                }
                catch
                {
                    continue;
                }

                if (ev is null)
                {
                    continue;
                }

                if (ev.Metadata is not null)
                {
                    Volatile.Write(ref _latestMetadata, ev.Metadata);
                }

                if (ev.Type == PtyControlEventTypes.Hello || ev.Type == PtyControlEventTypes.PermissionRequest)
                {
                    if (ev.Permission is not null)
                    {
                        Volatile.Write(ref _activePermission, ev.Permission);
                    }
                    if (ev.Type == PtyControlEventTypes.Hello)
                    {
                        _helloReceivedTcs.TrySetResult();
                    }
                }
                else if (ev.Type == PtyControlEventTypes.PermissionResolved)
                {
                    Volatile.Write(ref _activePermission, null);
                }

                if ((ev.Type == PtyControlEventTypes.Ack || ev.Type == PtyControlEventTypes.Error) &&
                    !string.IsNullOrWhiteSpace(ev.RequestId) &&
                    _pendingRequests.TryRemove(ev.RequestId, out var pendingTcs))
                {
                    pendingTcs.TrySetResult(ev);
                    continue;
                }

                _events.Writer.TryWrite(ev);
            }
        }
        catch
        {
            // Pipe closed
        }
        finally
        {
            _helloReceivedTcs.TrySetResult();
            _events.Writer.TryComplete();
            foreach (var (id, pendingTcs) in _pendingRequests)
            {
                if (_pendingRequests.TryRemove(id, out _))
                {
                    pendingTcs.TrySetException(new IOException("PTY control pipe disconnected."));
                }
            }
        }
    }

    private string NextRequestId()
        => $"req_{Interlocked.Increment(ref _requestCounter)}";

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

            try
            {
                await _pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }

            if (_readLoopTask is not null)
            {
                try
                {
                    await _readLoopTask.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
            }

            _lifetimeCts.Dispose();
        }
        finally
        {
            _disposeGate.Release();
        }
    }
}
