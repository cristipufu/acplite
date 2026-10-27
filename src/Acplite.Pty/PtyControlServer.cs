using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Acplite.Pty;

/// <summary>
/// Hosts a cross-platform local IPC server over <see cref="NamedPipeServerStream"/>
/// (<c>PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly</c>) for an active PTY session.
/// </summary>
public sealed class PtyControlServer : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _ptyInput;
    private readonly PtyTuiInspector _inspector;
    private readonly string? _registryDirectory;
    private readonly SemaphoreSlim _ptyWriteGate = new(1, 1);
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, ClientConnection> _clients = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private PtySessionMetadata _metadata;
    private Task? _acceptLoopTask;
    private long _nextClientId;
    private int _isDisposing;

    public PtyControlServer(
        PtySessionMetadata initialMetadata,
        Stream ptyInput,
        PtyTuiInspector inspector,
        string? registryDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(initialMetadata);
        ArgumentNullException.ThrowIfNull(ptyInput);
        ArgumentNullException.ThrowIfNull(inspector);

        _metadata = initialMetadata;
        _ptyInput = ptyInput;
        _inspector = inspector;
        _registryDirectory = registryDirectory;
    }

    public PtySessionMetadata Metadata => Volatile.Read(ref _metadata);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await PtySessionRegistry.SaveSessionAsync(_metadata, _registryDirectory, cancellationToken).ConfigureAwait(false);
        _acceptLoopTask = AcceptLoopAsync(_lifetimeCts.Token);
    }

    public async Task WriteToPtyInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await _ptyWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _ptyInput.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _ptyInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ptyWriteGate.Release();
        }
    }

    public async Task BroadcastPermissionRequestAsync(PtyPendingPermission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        await BroadcastEventAsync(new PtyControlEvent
        {
            Type = PtyControlEventTypes.PermissionRequest,
            RequestId = permission.RequestId,
            Permission = permission,
            Metadata = Metadata
        }).ConfigureAwait(false);
    }

    public async Task BroadcastPermissionResolvedAsync(string? requestId)
    {
        await BroadcastEventAsync(new PtyControlEvent
        {
            Type = PtyControlEventTypes.PermissionResolved,
            RequestId = requestId,
            Metadata = Metadata
        }).ConfigureAwait(false);
    }

    public async Task BroadcastExitedAsync(int exitCode)
    {
        await BroadcastEventAsync(new PtyControlEvent
        {
            Type = PtyControlEventTypes.Exited,
            ExitCode = exitCode,
            Metadata = Metadata
        }).ConfigureAwait(false);
    }

    public async Task BroadcastEventAsync(PtyControlEvent controlEvent)
    {
        if (Volatile.Read(ref _isDisposing) != 0)
        {
            return;
        }

        var line = JsonSerializer.Serialize(controlEvent, PtyJsonContext.Default.PtyControlEvent) + "\n";
        var bytes = Utf8NoBom.GetBytes(line);

        foreach (var (clientId, client) in _clients)
        {
            var sent = await client.TrySendAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            if (!sent)
            {
                if (_clients.TryRemove(clientId, out var removed))
                {
                    await removed.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? serverPipe = null;
            try
            {
                serverPipe = new NamedPipeServerStream(
                    _metadata.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await serverPipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var clientId = Interlocked.Increment(ref _nextClientId);
                var connection = new ClientConnection(serverPipe);
                serverPipe = null;
                _clients[clientId] = connection;

                connection.ReaderTask = HandleClientAsync(clientId, connection, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (serverPipe is not null)
                {
                    await serverPipe.DisposeAsync().ConfigureAwait(false);
                }
                break;
            }
            catch
            {
                if (serverPipe is not null)
                {
                    await serverPipe.DisposeAsync().ConfigureAwait(false);
                }
                try
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(long clientId, ClientConnection connection, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            var activePerm = _inspector.ActivePermission;
            await SendEventToClientAsync(connection, new PtyControlEvent
            {
                Type = PtyControlEventTypes.Hello,
                Metadata = Metadata,
                Permission = activePerm
            }, cancellationToken).ConfigureAwait(false);

            if (activePerm is not null)
            {
                await SendEventToClientAsync(connection, new PtyControlEvent
                {
                    Type = PtyControlEventTypes.PermissionRequest,
                    RequestId = activePerm.RequestId,
                    Permission = activePerm,
                    Metadata = Metadata
                }, cancellationToken).ConfigureAwait(false);
            }

            using var reader = new StreamReader(connection.Pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            while (!cancellationToken.IsCancellationRequested && connection.Pipe.IsConnected)
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

                PtyControlRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize(line, PtyJsonContext.Default.PtyControlRequest);
                }
                catch (Exception ex)
                {
                    await SendEventToClientAsync(connection, new PtyControlEvent
                    {
                        Type = PtyControlEventTypes.Error,
                        Success = false,
                        Message = $"Invalid control request JSON: {ex.Message}"
                    }, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (request is null)
                {
                    continue;
                }

                await ProcessRequestAsync(connection, request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Client disconnected
        }
        finally
        {
            if (_clients.TryRemove(clientId, out var removed))
            {
                await removed.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessRequestAsync(
        ClientConnection connection,
        PtyControlRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case PtyControlCommands.Ping:
            case PtyControlCommands.GetStatus:
                await SendEventToClientAsync(connection, new PtyControlEvent
                {
                    Type = PtyControlEventTypes.Ack,
                    RequestId = request.Id,
                    Success = true,
                    Metadata = Metadata,
                    Permission = _inspector.ActivePermission
                }, cancellationToken).ConfigureAwait(false);
                break;

            case PtyControlCommands.SendPrompt:
            case PtyControlCommands.Steer:
                if (string.IsNullOrWhiteSpace(request.Text))
                {
                    await SendEventToClientAsync(connection, new PtyControlEvent
                    {
                        Type = PtyControlEventTypes.Error,
                        RequestId = request.Id,
                        Success = false,
                        Message = "Prompt text cannot be empty."
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var clearedBeforePrompt = await _inspector.ClearActivePermissionAsync(cancellationToken).ConfigureAwait(false);
                if (clearedBeforePrompt is not null)
                {
                    await BroadcastPermissionResolvedAsync(clearedBeforePrompt.RequestId).ConfigureAwait(false);
                }

                await InjectPromptTextAsync(request.Text, cancellationToken).ConfigureAwait(false);
                await TouchMetadataAsync(cancellationToken).ConfigureAwait(false);

                await BroadcastEventAsync(new PtyControlEvent
                {
                    Type = PtyControlEventTypes.PromptInjected,
                    Message = request.Text,
                    Metadata = Metadata
                }).ConfigureAwait(false);

                await SendEventToClientAsync(connection, new PtyControlEvent
                {
                    Type = PtyControlEventTypes.Ack,
                    RequestId = request.Id,
                    Success = true,
                    Metadata = Metadata
                }, cancellationToken).ConfigureAwait(false);
                break;

            case PtyControlCommands.RespondPermission:
                var approved = request.Approved ?? true;
                var keystroke = _inspector.ResolveKeystroke(approved, request.SelectedOptionId);
                var clearedPerm = await _inspector.ClearActivePermissionAsync(cancellationToken).ConfigureAwait(false);

                var keystrokeBytes = Utf8NoBom.GetBytes(keystroke);
                await WriteToPtyInputAsync(keystrokeBytes, cancellationToken).ConfigureAwait(false);
                await TouchMetadataAsync(cancellationToken).ConfigureAwait(false);

                await BroadcastPermissionResolvedAsync(request.RequestId ?? clearedPerm?.RequestId).ConfigureAwait(false);

                await SendEventToClientAsync(connection, new PtyControlEvent
                {
                    Type = PtyControlEventTypes.Ack,
                    RequestId = request.Id,
                    Success = true,
                    Metadata = Metadata
                }, cancellationToken).ConfigureAwait(false);
                break;

            case PtyControlCommands.CancelTurn:
                var clearedOnCancel = await _inspector.ClearActivePermissionAsync(cancellationToken).ConfigureAwait(false);
                if (clearedOnCancel is not null)
                {
                    await BroadcastPermissionResolvedAsync(clearedOnCancel.RequestId).ConfigureAwait(false);
                }

                await WriteToPtyInputAsync("\x1b\x03"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await TouchMetadataAsync(cancellationToken).ConfigureAwait(false);

                await SendEventToClientAsync(connection, new PtyControlEvent
                {
                    Type = PtyControlEventTypes.Ack,
                    RequestId = request.Id,
                    Success = true,
                    Metadata = Metadata
                }, cancellationToken).ConfigureAwait(false);
                break;

            case PtyControlCommands.BindSession:
                await UpdateSessionBindingAsync(request.SessionId, request.TranscriptPath, cancellationToken).ConfigureAwait(false);
                await BroadcastEventAsync(new PtyControlEvent
                {
                    Type = PtyControlEventTypes.SessionBound,
                    Metadata = Metadata
                }).ConfigureAwait(false);

                await SendEventToClientAsync(connection, new PtyControlEvent
                {
                    Type = PtyControlEventTypes.Ack,
                    RequestId = request.Id,
                    Success = true,
                    Metadata = Metadata
                }, cancellationToken).ConfigureAwait(false);
                break;

            default:
                await SendEventToClientAsync(connection, new PtyControlEvent
                {
                    Type = PtyControlEventTypes.Error,
                    RequestId = request.Id,
                    Success = false,
                    Message = $"Unknown PTY control command '{request.Command}'."
                }, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task InjectPromptTextAsync(string promptText, CancellationToken cancellationToken)
    {
        var normalized = promptText.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n');
        var textBytes = Utf8NoBom.GetBytes(normalized);

        await _ptyWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _ptyInput.WriteAsync(textBytes, cancellationToken).ConfigureAwait(false);
            await _ptyInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Separate the text payload from the trailing carriage return by a brief interval so
            // TUI input loops (Ink, Ratatui, BubbleTea) process the text before receiving Enter.
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);

            await _ptyInput.WriteAsync("\r"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await _ptyInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ptyWriteGate.Release();
        }
    }

    public async Task TouchMetadataAsync(CancellationToken cancellationToken = default)
    {
        await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _metadata);
            var updated = current with { LastActiveAt = DateTimeOffset.UtcNow };
            Volatile.Write(ref _metadata, updated);
            await PtySessionRegistry.SaveSessionAsync(updated, _registryDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    public async Task UpdateSessionBindingAsync(
        string? sessionId,
        string? transcriptPath,
        CancellationToken cancellationToken = default)
    {
        await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _metadata);
            var updated = current with
            {
                SessionId = !string.IsNullOrWhiteSpace(sessionId) ? sessionId : current.SessionId,
                TranscriptPath = !string.IsNullOrWhiteSpace(transcriptPath) ? transcriptPath : current.TranscriptPath,
                LastActiveAt = DateTimeOffset.UtcNow
            };
            Volatile.Write(ref _metadata, updated);
            await PtySessionRegistry.SaveSessionAsync(updated, _registryDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    private static async Task SendEventToClientAsync(
        ClientConnection connection,
        PtyControlEvent controlEvent,
        CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(controlEvent, PtyJsonContext.Default.PtyControlEvent) + "\n";
        var bytes = Utf8NoBom.GetBytes(line);
        _ = await connection.TrySendAsync(bytes, cancellationToken).ConfigureAwait(false);
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

            PtySessionRegistry.RemoveSession(_metadata.Pid, _registryDirectory);
            await _lifetimeCts.CancelAsync().ConfigureAwait(false);

            foreach (var (clientId, client) in _clients)
            {
                if (_clients.TryRemove(clientId, out var removed))
                {
                    await removed.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (_acceptLoopTask is not null)
            {
                try
                {
                    await _acceptLoopTask.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore timeout
                }
            }

            _lifetimeCts.Dispose();
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private sealed class ClientConnection(NamedPipeServerStream pipe) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private int _disposed;

        public NamedPipeServerStream Pipe => pipe;
        public Task? ReaderTask { get; set; }

        public async Task<bool> TrySendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0 || !pipe.IsConnected)
            {
                return false;
            }

            var acquired = false;
            try
            {
                await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;

                if (Volatile.Read(ref _disposed) != 0 || !pipe.IsConnected)
                {
                    return false;
                }

                await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (acquired)
                {
                    _writeGate.Release();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }

            if (ReaderTask is not null)
            {
                try
                {
                    await ReaderTask.WaitAsync(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
            }
        }
    }
}
