using System.Buffers;
using System.Runtime.InteropServices;

namespace Acplite.Pty;

/// <summary>
/// Runs an interactive coding agent inside a transparent pseudo-terminal (PTY),
/// forwarding terminal I/O and window resize signals while exposing a local
/// <see cref="PtyControlServer"/> for mobile/remote inspection and input injection.
/// </summary>
public static class PtySessionRunner
{
    private const int BufferSize = 16 * 1024;
    private const short POLLIN = 0x0001;
    private const int EINTR = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    public static async Task<int> RunInteractiveAsync(
        string providerShortId,
        string binaryPath,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? sessionIdHint = null,
        string? registryDirectory = null,
        Stream? customStdin = null,
        Stream? customStdout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerShortId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        ArgumentNullException.ThrowIfNull(arguments);

        var cwd = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : Environment.CurrentDirectory;

        var inferredSessionId = sessionIdHint ?? TryExtractSessionIdFromArgs(arguments);
        var initialSize = PtyFactory.GetControllingTerminalSize();

        await using var pty = PtyFactory.Start(new PtyStartOptions
        {
            FileName = binaryPath,
            Arguments = arguments,
            WorkingDirectory = cwd,
            InitialSize = initialSize
        });

        var inspector = new PtyTuiInspector();
        var now = DateTimeOffset.UtcNow;
        var pipeName = $"acplite-{Environment.ProcessId}";
        var metadata = new PtySessionMetadata
        {
            Pid = Environment.ProcessId,
            ChildPid = pty.ProcessId,
            Provider = providerShortId.ToLowerInvariant(),
            PipeName = pipeName,
            WorkingDirectory = cwd,
            SessionId = inferredSessionId,
            TranscriptPath = null,
            StartedAt = now,
            LastActiveAt = now
        };

        await using var controlServer = new PtyControlServer(metadata, pty.InputStream, inspector, registryDirectory);
        await controlServer.StartAsync(cancellationToken).ConfigureAwait(false);

        using var rawModeRestorer = customStdin is null ? PtyFactory.TryEnterRawTerminalMode() : null;
        await using var resizeWatcher = RegisterWindowResizeHandler(pty);
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var stdout = customStdout ?? Console.OpenStandardOutput();
        var outputPumpTask = PumpPtyOutputAsync(pty.OutputStream, stdout, inspector, controlServer, pumpCts.Token);
        var inputPumpTask = PumpParentInputAsync(customStdin, inspector, controlServer, pumpCts.Token);

        int exitCode;
        try
        {
            exitCode = await pty.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await pumpCts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await outputPumpTask.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore timeout on shutdown
        }

        try
        {
            await inputPumpTask.WaitAsync(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore timeout if customStdin was blocking
        }

        await controlServer.BroadcastExitedAsync(exitCode).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task PumpPtyOutputAsync(
        Stream ptyOutput,
        Stream parentStdout,
        PtyTuiInspector inspector,
        PtyControlServer controlServer,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await ptyOutput.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await parentStdout.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await parentStdout.FlushAsync(cancellationToken).ConfigureAwait(false);

                var detectedPermission = await inspector
                    .ProcessOutputChunkAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);

                if (detectedPermission is not null)
                {
                    await controlServer.BroadcastPermissionRequestAsync(detectedPermission).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit
        }
        catch
        {
            // Stream closed on child exit
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task PumpParentInputAsync(
        Stream? customStdin,
        PtyTuiInspector inspector,
        PtyControlServer controlServer,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            if (customStdin is null && (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()))
            {
                await PumpPosixStdinAsync(buffer, inspector, controlServer, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (customStdin is null && OperatingSystem.IsWindows() && !Console.IsInputRedirected)
            {
                await PumpWindowsInteractiveStdinAsync(buffer, inspector, controlServer, cancellationToken).ConfigureAwait(false);
                return;
            }

            var stdin = customStdin ?? Console.OpenStandardInput();
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stdin.ReadAsync(buffer.AsMemory(0, 4096), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await HandleLocalStdinBytesAsync(buffer.AsMemory(0, read), inspector, controlServer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch
        {
            // Stdin closed
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task PumpPosixStdinAsync(
        byte[] buffer,
        PtyTuiInspector inspector,
        PtyControlServer controlServer,
        CancellationToken cancellationToken)
    {
        if (isatty(0) != 1)
        {
            var stdin = Console.OpenStandardInput();
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stdin.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }
                await HandleLocalStdinBytesAsync(buffer.AsMemory(0, read), inspector, controlServer, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = PollAndReadStdinNonBlocking(buffer);
            if (bytesRead < 0)
            {
                break;
            }

            if (bytesRead == 0)
            {
                await Task.Delay(12, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await HandleLocalStdinBytesAsync(buffer.AsMemory(0, bytesRead), inspector, controlServer, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PumpWindowsInteractiveStdinAsync(
        byte[] buffer,
        PtyTuiInspector inspector,
        PtyControlServer controlServer,
        CancellationToken cancellationToken)
    {
        var stdin = Console.OpenStandardInput();
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stdin.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await HandleLocalStdinBytesAsync(buffer.AsMemory(0, read), inspector, controlServer, cancellationToken).ConfigureAwait(false);
        }
    }

    private static unsafe int PollAndReadStdinNonBlocking(byte[] buffer)
    {
        var pfd = new PollFd
        {
            Fd = 0,
            Events = POLLIN
        };

        // Zero timeout: non-blocking check so no thread-pool thread is held waiting on stdin
        var pollRes = poll(&pfd, 1, 0);
        if (pollRes < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            return err == EINTR ? 0 : -1;
        }

        if (pollRes == 0 || (pfd.Revents & POLLIN) == 0)
        {
            return 0;
        }

        fixed (byte* ptr = buffer)
        {
            var n = read(0, ptr, (nuint)buffer.Length);
            if (n > 0)
            {
                return (int)n;
            }
            if (n == 0)
            {
                return -1;
            }

            var err = Marshal.GetLastPInvokeError();
            return err == EINTR ? 0 : -1;
        }
    }

    private static async Task HandleLocalStdinBytesAsync(
        ReadOnlyMemory<byte> bytes,
        PtyTuiInspector inspector,
        PtyControlServer controlServer,
        CancellationToken cancellationToken)
    {
        var cleared = await inspector.ClearActivePermissionAsync(cancellationToken).ConfigureAwait(false);
        if (cleared is not null)
        {
            await controlServer.BroadcastPermissionResolvedAsync(cleared.RequestId).ConfigureAwait(false);
        }

        await controlServer.WriteToPtyInputAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static IAsyncDisposable RegisterWindowResizeHandler(IPtyProcess pty)
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            try
            {
                var reg = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, ctx =>
                {
                    ctx.Cancel = true;
                    var newSize = PtyFactory.GetControllingTerminalSize();
                    pty.Resize(newSize);
                });
                return new SyncDisposableAdapter(reg);
            }
            catch
            {
                return NoOpAsyncDisposable.Instance;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsResizePoller(pty);
        }

        return NoOpAsyncDisposable.Instance;
    }

    internal static string? TryExtractSessionIdFromArgs(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if ((string.Equals(arg, "--resume", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(arg, "-r", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(arg, "--conversation", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(arg, "--session", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < args.Count &&
                !args[i + 1].StartsWith('-'))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private sealed class SyncDisposableAdapter(IDisposable inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public static NoOpAsyncDisposable Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WindowsResizePoller : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pollTask;
        private int _disposed;

        public WindowsResizePoller(IPtyProcess pty)
        {
            _pollTask = PollLoopAsync(pty, _cts.Token);
        }

        private static async Task PollLoopAsync(IPtyProcess pty, CancellationToken cancellationToken)
        {
            await Task.Yield();
            var lastSize = PtyFactory.GetControllingTerminalSize();
            while (!cancellationToken.IsCancellationRequested && !pty.HasExited)
            {
                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    var current = PtyFactory.GetControllingTerminalSize();
                    if (current != lastSize)
                    {
                        lastSize = current;
                        pty.Resize(current);
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await _pollTask.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }
            _cts.Dispose();
        }
    }

    [DllImport("libc")]
    private static extern int isatty(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int poll(PollFd* fds, uint nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint read(int fd, void* buf, nuint count);
}
