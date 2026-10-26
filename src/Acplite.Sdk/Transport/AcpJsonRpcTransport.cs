using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Acplite.Sdk.Transport;

public sealed class JsonRpcRemoteException : Exception
{
    public int Code { get; }
    public JsonElement? ErrorData { get; }

    public JsonRpcRemoteException(int code, string message, JsonElement? errorData = null)
        : base($"JSON-RPC error {code}: {message}")
    {
        Code = code;
        ErrorData = errorData;
    }
}

/// <summary>
/// Multiplexes newline-delimited JSON-RPC 2.0 and stream-json frames over a pair of streams.
/// All writes are serialized with <see cref="SemaphoreSlim"/> (<c>WaitAsync</c>) and written
/// as a single atomic newline-terminated UTF-8 payload.
/// </summary>
public sealed class AcpJsonRpcTransport : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly StreamReader _reader;
    private readonly Stream _outputStream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _inFlightServerRequests = new();
    private readonly CancellationTokenSource _readLoopCts = new();
    private Task? _readLoopTask;
    private long _nextRequestId;
    private long _nextInFlightId;
    private int _isStarted;
    private int _isDisposed;

    public Func<JsonElement, string, JsonElement?, CancellationToken, Task<JsonElement?>>? OnRequestAsync { get; set; }
    public Func<string, JsonElement?, CancellationToken, Task>? OnNotificationAsync { get; set; }
    public Func<JsonElement, CancellationToken, Task>? OnRawFrameAsync { get; set; }
    public Action<Exception>? OnTransportError { get; set; }
    public Action? OnInputStreamClosed { get; set; }

    public AcpJsonRpcTransport(Stream inputStream, Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(inputStream);
        ArgumentNullException.ThrowIfNull(outputStream);

        _reader = new StreamReader(
            inputStream,
            Utf8NoBom,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 8192,
            leaveOpen: true);
        _outputStream = outputStream;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        if (Interlocked.CompareExchange(ref _isStarted, 1, 0) == 0)
        {
            _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
        }
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        JsonElement? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        var id = Interlocked.Increment(ref _nextRequestId).ToString();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        try
        {
            using var ms = new MemoryStream();
            await using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("id", id);
                writer.WriteString("method", method);
                if (parameters.HasValue && parameters.Value.ValueKind != JsonValueKind.Undefined)
                {
                    writer.WritePropertyName("params");
                    parameters.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            ms.WriteByte((byte)'\n');

            await WriteRawLineBytesAsync(ms.GetBuffer(), (int)ms.Length, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    public async Task SendNotificationAsync(
        string method,
        JsonElement? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            if (parameters.HasValue && parameters.Value.ValueKind != JsonValueKind.Undefined)
            {
                writer.WritePropertyName("params");
                parameters.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        ms.WriteByte((byte)'\n');

        await WriteRawLineBytesAsync(ms.GetBuffer(), (int)ms.Length, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendResponseAsync(
        JsonElement idElement,
        JsonElement? result,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            idElement.WriteTo(writer);
            writer.WritePropertyName("result");
            if (result.HasValue && result.Value.ValueKind != JsonValueKind.Undefined)
            {
                result.Value.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        ms.WriteByte((byte)'\n');

        await WriteRawLineBytesAsync(ms.GetBuffer(), (int)ms.Length, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendErrorResponseAsync(
        JsonElement idElement,
        int code,
        string message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            idElement.WriteTo(writer);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        ms.WriteByte((byte)'\n');

        await WriteRawLineBytesAsync(ms.GetBuffer(), (int)ms.Length, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendRawJsonLineAsync(string jsonLine, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonLine);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        var maxBytes = Utf8NoBom.GetMaxByteCount(jsonLine.Length) + 1;
        var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            var written = Utf8NoBom.GetBytes(jsonLine.AsSpan(), rented.AsSpan());
            rented[written] = (byte)'\n';
            await WriteRawLineBytesAsync(rented, written + 1, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task WriteRawLineBytesAsync(byte[] payload, int count, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

            await _outputStream.WriteAsync(payload.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            await _outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var reachedCleanEof = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    reachedCleanEof = true;
                    break;
                }

                var trimmed = line.AsSpan().Trim();
                if (trimmed.IsEmpty || trimmed[0] != '{')
                {
                    continue;
                }

                JsonDocument? doc = null;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement.Clone();
                    await DispatchFrameAsync(root, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _isDisposed) == 0)
            {
                OnTransportError?.Invoke(ex);
            }
        }
        finally
        {
            FailAllPendingRequests(new EndOfStreamException("The underlying agent transport stream closed."));
            if (reachedCleanEof && Volatile.Read(ref _isDisposed) == 0)
            {
                OnInputStreamClosed?.Invoke();
            }
        }
    }

    private async Task DispatchFrameAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var hasMethod = root.TryGetProperty("method", out var methodProp) && methodProp.ValueKind == JsonValueKind.String;
        var hasId = root.TryGetProperty("id", out var idProp) && idProp.ValueKind is JsonValueKind.String or JsonValueKind.Number;
        var hasResult = root.TryGetProperty("result", out var resultProp);
        var hasError = root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.Object;

        if (!hasMethod && hasId && (hasResult || hasError))
        {
            var idKey = idProp.ValueKind == JsonValueKind.String ? idProp.GetString()! : idProp.GetRawText();
            if (_pendingRequests.TryRemove(idKey, out var tcs))
            {
                if (hasError)
                {
                    var code = errorProp.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : -32000;
                    var msg = errorProp.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() ?? "Unknown JSON-RPC error"
                        : "Unknown JSON-RPC error";
                    JsonElement? data = errorProp.TryGetProperty("data", out var d) ? d.Clone() : null;
                    tcs.TrySetException(new JsonRpcRemoteException(code, msg, data));
                }
                else
                {
                    tcs.TrySetResult(hasResult ? resultProp.Clone() : default);
                }
            }
            return;
        }

        if (hasMethod && hasId)
        {
            var method = methodProp.GetString()!;
            JsonElement? parameters = root.TryGetProperty("params", out var p) ? p.Clone() : null;
            var idClone = idProp.Clone();

            if (OnRequestAsync is not null)
            {
                var trackingId = Interlocked.Increment(ref _nextInFlightId);
                var task = HandleServerRequestCoreAsync(trackingId, idClone, method, parameters, cancellationToken);
                _inFlightServerRequests[trackingId] = task;
            }
            return;
        }

        if (hasMethod && !hasId)
        {
            var method = methodProp.GetString()!;
            JsonElement? parameters = root.TryGetProperty("params", out var p) ? p.Clone() : null;
            if (OnNotificationAsync is not null)
            {
                await OnNotificationAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            }
        }

        if (OnRawFrameAsync is not null)
        {
            await OnRawFrameAsync(root, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleServerRequestCoreAsync(
        long trackingId,
        JsonElement idClone,
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            var handler = OnRequestAsync;
            if (handler is null)
            {
                return;
            }

            var responseResult = await handler(idClone, method, parameters, cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _isDisposed) == 0)
            {
                await SendResponseAsync(idClone, responseResult, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Transport shutting down
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _isDisposed) == 0)
            {
                try
                {
                    await SendErrorResponseAsync(idClone, -32603, ex.Message, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Stream may have closed concurrently; ignore secondary write error
                }
            }
        }
        finally
        {
            _inFlightServerRequests.TryRemove(trackingId, out _);
        }
    }

    private void FailAllPendingRequests(Exception error)
    {
        foreach (var (key, _) in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(error);
            }
        }
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

            await _readLoopCts.CancelAsync().ConfigureAwait(false);
            FailAllPendingRequests(new ObjectDisposedException(nameof(AcpJsonRpcTransport)));

            var readLoopCompleted = true;
            if (_readLoopTask is not null)
            {
                try
                {
                    await _readLoopTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
                catch
                {
                    readLoopCompleted = _readLoopTask.IsCompleted;
                }
            }

            foreach (var (_, task) in _inFlightServerRequests)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore timeout/cancellation during teardown
                }
            }
            _inFlightServerRequests.Clear();

            if (readLoopCompleted)
            {
                _reader.Dispose();
                _readLoopCts.Dispose();
            }
        }
        finally
        {
            _disposeGate.Release();
        }
    }
}
