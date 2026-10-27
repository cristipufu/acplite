using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Acplite.Pty;

[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
internal sealed class PosixPtyProcess : IPtyProcess
{
    private const int O_RDWR = 0x0002;
    private const int O_NONBLOCK_MACOS = 0x0004;
    private const int O_NONBLOCK_LINUX = 0x0800;
    private const int O_NOCTTY_MACOS = 0x20000;
    private const int O_NOCTTY_LINUX = 0x0100;
    private const int F_GETFD = 1;
    private const int F_SETFD = 2;
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;
    private const int FD_CLOEXEC = 1;

    private const ulong TIOCGWINSZ_MACOS = 0x40087468UL;
    private const ulong TIOCGWINSZ_LINUX = 0x5413UL;

    private const ulong TIOCSWINSZ_MACOS = 0x80087467UL;
    private const ulong TIOCSWINSZ_LINUX = 0x5414UL;

    private const int EINTR = 4;
    private const int EIO = 5;
    private const int EBADF = 9;
    private const int EAGAIN_LINUX = 11;
    private const int EAGAIN_MACOS = 35;

    private const int WNOHANG = 1;
    private const int SIGKILL = 9;
    private const int SIGHUP = 1;
    private const short POSIX_SPAWN_SETSID_MACOS = 0x0400;
    private const short POSIX_SPAWN_SETSID_LINUX = 0x0080;
    private const short POLLIN = 0x0001;
    private const short POLLOUT = 0x0004;
    private const short POLLERR = 0x0008;
    private const short POLLHUP = 0x0010;
    private const short POLLNVAL = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Row;
        public ushort Col;
        public ushort XPixel;
        public ushort YPixel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    private readonly int _childPid;
    private readonly PosixPtyStream _stream;
    private readonly Task<int> _exitTask;
    private int _masterFd;
    private int _exitCode;
    private int _hasExited;
    private int _isDisposed;

    private PosixPtyProcess(int masterFd, int childPid)
    {
        _masterFd = masterFd;
        _childPid = childPid;
        _stream = new PosixPtyStream(this);
        _exitTask = MonitorChildExitAsync();
    }

    public int ProcessId => _childPid;
    public bool HasExited => Volatile.Read(ref _hasExited) != 0;
    public Stream InputStream => _stream;
    public Stream OutputStream => _stream;
    internal int MasterFd => Volatile.Read(ref _masterFd);

    public static unsafe PosixPtyProcess Start(PtyStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);

        var isMac = OperatingSystem.IsMacOS();
        var oNoCtty = isMac ? O_NOCTTY_MACOS : O_NOCTTY_LINUX;
        var oNonBlock = isMac ? O_NONBLOCK_MACOS : O_NONBLOCK_LINUX;
        var tiocSwinsz = (nuint)(isMac ? TIOCSWINSZ_MACOS : TIOCSWINSZ_LINUX);

        var masterFd = posix_openpt(O_RDWR | oNoCtty);
        if (masterFd < 0)
        {
            throw new InvalidOperationException($"posix_openpt failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            var currentFdFlags = fcntl_vararg(masterFd, F_GETFD, 0, 0, 0, 0, 0, 0, 0);
            if (currentFdFlags >= 0)
            {
                var nextFdFlags = currentFdFlags | FD_CLOEXEC;
                _ = fcntl_vararg(masterFd, F_SETFD, nextFdFlags, 0, 0, 0, 0, 0, nextFdFlags);
            }

            var currentFlFlags = fcntl_vararg(masterFd, F_GETFL, 0, 0, 0, 0, 0, 0, 0);
            if (currentFlFlags >= 0)
            {
                var nextFlFlags = currentFlFlags | oNonBlock;
                _ = fcntl_vararg(masterFd, F_SETFL, nextFlFlags, 0, 0, 0, 0, 0, nextFlFlags);
            }

            if (grantpt(masterFd) != 0)
            {
                throw new InvalidOperationException($"grantpt failed with errno {Marshal.GetLastPInvokeError()}.");
            }

            if (unlockpt(masterFd) != 0)
            {
                throw new InvalidOperationException($"unlockpt failed with errno {Marshal.GetLastPInvokeError()}.");
            }

            var slaveNamePtr = ptsname(masterFd);
            var slavePath = Marshal.PtrToStringUTF8(slaveNamePtr);
            if (string.IsNullOrWhiteSpace(slavePath))
            {
                throw new InvalidOperationException("ptsname returned an empty slave PTY path.");
            }

            var clampedSize = options.InitialSize.Clamp();
            var ws = new WinSize
            {
                Row = (ushort)clampedSize.Rows,
                Col = (ushort)clampedSize.Columns
            };
            var wsPtr = (nint)(&ws);
            _ = ioctl_vararg(masterFd, tiocSwinsz, wsPtr, 0, 0, 0, 0, 0, wsPtr);

            var cwd = Directory.Exists(options.WorkingDirectory)
                ? options.WorkingDirectory
                : Environment.CurrentDirectory;

            var argCount = options.Arguments.Count + 1;
            var envList = BuildEnvironmentVariables(options.Environment);

            nint filePtr = 0;
            nint slavePathPtr = 0;
            nint cwdPtr = 0;
            var slaveFd = -1;
            nint* argvPtr = null;
            nint* envpPtr = null;
            nint actionsPtr = 0;
            nint attrPtr = 0;
            var actionsInitialized = false;
            var attrInitialized = false;

            try
            {
                filePtr = Marshal.StringToCoTaskMemUTF8(options.FileName);
                slavePathPtr = Marshal.StringToCoTaskMemUTF8(slavePath);
                cwdPtr = Marshal.StringToCoTaskMemUTF8(cwd);

                slaveFd = open((byte*)slavePathPtr, O_RDWR | oNoCtty);
                if (slaveFd < 0)
                {
                    throw new InvalidOperationException($"open(slavePty) failed with errno {Marshal.GetLastPInvokeError()}.");
                }

                _ = ioctl_vararg(slaveFd, tiocSwinsz, wsPtr, 0, 0, 0, 0, 0, wsPtr);

                argvPtr = (nint*)NativeMemory.AllocZeroed((nuint)(argCount + 1), (nuint)sizeof(nint));
                envpPtr = (nint*)NativeMemory.AllocZeroed((nuint)(envList.Count + 1), (nuint)sizeof(nint));
                actionsPtr = (nint)NativeMemory.AllocZeroed(512);
                attrPtr = (nint)NativeMemory.AllocZeroed(512);

                argvPtr[0] = Marshal.StringToCoTaskMemUTF8(options.FileName);
                for (var i = 0; i < options.Arguments.Count; i++)
                {
                    argvPtr[i + 1] = Marshal.StringToCoTaskMemUTF8(options.Arguments[i]);
                }
                argvPtr[argCount] = 0;

                for (var i = 0; i < envList.Count; i++)
                {
                    envpPtr[i] = Marshal.StringToCoTaskMemUTF8(envList[i]);
                }
                envpPtr[envList.Count] = 0;

                if (posix_spawn_file_actions_init(actionsPtr) != 0)
                {
                    throw new InvalidOperationException("posix_spawn_file_actions_init failed.");
                }
                actionsInitialized = true;

                if (posix_spawnattr_init(attrPtr) != 0)
                {
                    throw new InvalidOperationException("posix_spawnattr_init failed.");
                }
                attrInitialized = true;

                var setSidFlag = isMac ? POSIX_SPAWN_SETSID_MACOS : POSIX_SPAWN_SETSID_LINUX;
                _ = posix_spawnattr_setflags(attrPtr, setSidFlag);

                // Open slavePath without O_NOCTTY as fd 0 in the new session so it becomes the controlling TTY
                _ = posix_spawn_file_actions_addopen(actionsPtr, 0, (byte*)slavePathPtr, O_RDWR, 0);
                _ = posix_spawn_file_actions_adddup2(actionsPtr, 0, 1);
                _ = posix_spawn_file_actions_adddup2(actionsPtr, 0, 2);
                if (slaveFd > 2)
                {
                    _ = posix_spawn_file_actions_addclose(actionsPtr, slaveFd);
                }
                if (masterFd > 2)
                {
                    _ = posix_spawn_file_actions_addclose(actionsPtr, masterFd);
                }

                try
                {
                    _ = posix_spawn_file_actions_addchdir_np(actionsPtr, (byte*)cwdPtr);
                }
                catch (EntryPointNotFoundException)
                {
                    // Fallback on older libc builds without addchdir_np
                }

                var spawnErr = posix_spawn(
                    out var pid,
                    (byte*)filePtr,
                    actionsPtr,
                    attrPtr,
                    (byte**)argvPtr,
                    (byte**)envpPtr);

                if (spawnErr != 0 || pid <= 0)
                {
                    throw new InvalidOperationException($"posix_spawn failed with error {spawnErr}.");
                }

                return new PosixPtyProcess(masterFd, pid);
            }
            finally
            {
                if (actionsInitialized)
                {
                    _ = posix_spawn_file_actions_destroy(actionsPtr);
                }
                if (attrInitialized)
                {
                    _ = posix_spawnattr_destroy(attrPtr);
                }
                if (actionsPtr != 0)
                {
                    NativeMemory.Free((void*)actionsPtr);
                }
                if (attrPtr != 0)
                {
                    NativeMemory.Free((void*)attrPtr);
                }
                if (slaveFd >= 0)
                {
                    _ = close(slaveFd);
                }
                if (filePtr != 0)
                {
                    Marshal.FreeCoTaskMem(filePtr);
                }
                if (slavePathPtr != 0)
                {
                    Marshal.FreeCoTaskMem(slavePathPtr);
                }
                if (cwdPtr != 0)
                {
                    Marshal.FreeCoTaskMem(cwdPtr);
                }

                if (argvPtr is not null)
                {
                    for (var i = 0; i < argCount; i++)
                    {
                        if (argvPtr[i] != 0)
                        {
                            Marshal.FreeCoTaskMem(argvPtr[i]);
                        }
                    }
                    NativeMemory.Free(argvPtr);
                }

                if (envpPtr is not null)
                {
                    for (var i = 0; i < envList.Count; i++)
                    {
                        if (envpPtr[i] != 0)
                        {
                            Marshal.FreeCoTaskMem(envpPtr[i]);
                        }
                    }
                    NativeMemory.Free(envpPtr);
                }
            }
        }
        catch
        {
            _ = close(masterFd);
            throw;
        }
    }

    public unsafe void Resize(TerminalSize size)
    {
        var fd = Volatile.Read(ref _masterFd);
        if (fd < 0 || HasExited)
        {
            return;
        }

        var clamped = size.Clamp();
        var ws = new WinSize
        {
            Row = (ushort)clamped.Rows,
            Col = (ushort)clamped.Columns
        };
        var tiocSwinsz = (nuint)(OperatingSystem.IsMacOS() ? TIOCSWINSZ_MACOS : TIOCSWINSZ_LINUX);
        var wsPtr = (nint)(&ws);
        _ = ioctl_vararg(fd, tiocSwinsz, wsPtr, 0, 0, 0, 0, 0, wsPtr);
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        => await _exitTask.WaitAsync(cancellationToken).ConfigureAwait(false);

    public void Kill()
    {
        if (HasExited)
        {
            return;
        }

        try
        {
            _ = kill(-_childPid, SIGHUP);
            _ = kill(_childPid, SIGKILL);
        }
        catch
        {
            // Ignore
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        var fd = Interlocked.Exchange(ref _masterFd, -1);
        if (fd >= 0)
        {
            _ = close(fd);
        }

        if (!HasExited)
        {
            Kill();
        }

        try
        {
            await _exitTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore timeout on teardown
        }
    }

    private async Task<int> MonitorChildExitAsync()
    {
        await Task.Yield();
        while (true)
        {
            var res = TryPollChildExit(out var code, out var fatal);
            if (res)
            {
                Volatile.Write(ref _exitCode, code);
                Volatile.Write(ref _hasExited, 1);
                return code;
            }

            if (fatal)
            {
                Volatile.Write(ref _hasExited, 1);
                return Volatile.Read(ref _exitCode);
            }

            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private unsafe bool TryPollChildExit(out int exitCode, out bool fatalError)
    {
        exitCode = 0;
        fatalError = false;
        var status = 0;
        var res = waitpid(_childPid, &status, WNOHANG);
        if (res == _childPid)
        {
            exitCode = ExtractExitCode(status);
            return true;
        }

        if (res < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err != EINTR)
            {
                fatalError = true;
            }
        }

        return false;
    }

    private static int ExtractExitCode(int status)
    {
        var sig = status & 0x7F;
        if (sig == 0)
        {
            return (status >> 8) & 0xFF;
        }
        return 128 + sig;
    }

    private static List<string> BuildEnvironmentVariables(IReadOnlyDictionary<string, string>? overrides)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v)
            {
                dict[k] = v;
            }
        }

        dict.Remove("MallocStackLogging");
        dict.Remove("MallocStackLoggingNoCompact");
        dict.Remove("MallocScribble");

        if (!dict.ContainsKey("TERM") || string.IsNullOrWhiteSpace(dict["TERM"]) || dict["TERM"] == "dumb")
        {
            dict["TERM"] = "xterm-256color";
        }

        dict["ACPLITE_PTY_ACTIVE"] = "1";

        if (overrides is not null)
        {
            foreach (var (k, v) in overrides)
            {
                dict[k] = v;
            }
        }

        var list = new List<string>(dict.Count);
        foreach (var (k, v) in dict)
        {
            list.Add($"{k}={v}");
        }
        return list;
    }

    public static unsafe TerminalSize TryGetControllingTerminalSize()
    {
        var tiocGwinsz = (nuint)(OperatingSystem.IsMacOS() ? TIOCGWINSZ_MACOS : TIOCGWINSZ_LINUX);
        var ws = new WinSize();
        var wsPtr = (nint)(&ws);

        for (var fd = 0; fd <= 2; fd++)
        {
            if (isatty(fd) == 1 &&
                ioctl_vararg(fd, tiocGwinsz, wsPtr, 0, 0, 0, 0, 0, wsPtr) == 0 &&
                ws.Col > 0 && ws.Row > 0)
            {
                return new TerminalSize(ws.Col, ws.Row).Clamp();
            }
        }

        try
        {
            if (Console.WindowWidth > 0 && Console.WindowHeight > 0)
            {
                return new TerminalSize(Console.WindowWidth, Console.WindowHeight).Clamp();
            }
        }
        catch
        {
            // Ignore when stdout is redirected
        }

        return TerminalSize.Default;
    }

    public static unsafe IDisposable? TryEnterRawTerminalMode()
    {
        if (isatty(0) != 1)
        {
            return null;
        }

        var original = new byte[128];
        var raw = new byte[128];

        fixed (byte* origPtr = original)
        fixed (byte* rawPtr = raw)
        {
            if (tcgetattr(0, origPtr) != 0)
            {
                return null;
            }

            Buffer.MemoryCopy(origPtr, rawPtr, 128, 128);
            cfmakeraw(rawPtr);
            if (tcsetattr(0, 0 /* TCSANOW */, rawPtr) != 0)
            {
                return null;
            }
        }

        return new RawModeRestorer(original);
    }

    private sealed class RawModeRestorer(byte[] originalTermios) : IDisposable
    {
        private int _disposed;

        public unsafe void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            fixed (byte* ptr = originalTermios)
            {
                _ = tcsetattr(0, 0 /* TCSANOW */, ptr);
            }
        }
    }

    private sealed class PosixPtyStream(PosixPtyProcess owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            var span = buffer.AsSpan(offset, count);
            while (true)
            {
                var status = TryReadNonBlocking(owner, span, out var bytesRead);
                if (status != PollReadStatus.WouldBlock)
                {
                    return bytesRead;
                }
                Thread.Sleep(5);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            var idleDelayMs = 4;
            while (!cancellationToken.IsCancellationRequested)
            {
                var status = TryReadNonBlocking(owner, buffer.Span, out var bytesRead);
                if (status != PollReadStatus.WouldBlock)
                {
                    return bytesRead;
                }

                await Task.Delay(idleDelayMs, cancellationToken).ConfigureAwait(false);
                if (idleDelayMs < 32)
                {
                    idleDelayMs *= 2;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            var remaining = buffer.AsSpan(offset, count);
            while (remaining.Length > 0)
            {
                var written = TryWriteNonBlocking(owner, remaining);
                if (written < 0)
                {
                    return;
                }
                if (written == 0)
                {
                    Thread.Sleep(2);
                    continue;
                }
                remaining = remaining[written..];
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = buffer;
            while (remaining.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var written = TryWriteNonBlocking(owner, remaining.Span);
                if (written < 0)
                {
                    return;
                }

                if (written > 0)
                {
                    remaining = remaining[written..];
                    continue;
                }

                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }

        private enum PollReadStatus
        {
            DataOrEof,
            WouldBlock
        }

        private static unsafe PollReadStatus TryReadNonBlocking(
            PosixPtyProcess owner,
            Span<byte> buffer,
            out int bytesRead)
        {
            bytesRead = 0;
            var fd = owner.MasterFd;
            if (fd < 0)
            {
                return PollReadStatus.DataOrEof;
            }

            var pfd = new PollFd
            {
                Fd = fd,
                Events = POLLIN
            };

            // Zero timeout: non-blocking readiness check without holding a thread-pool thread
            var pollRes = poll(&pfd, 1, 0);
            if (pollRes < 0)
            {
                var err = Marshal.GetLastPInvokeError();
                return err == EINTR ? PollReadStatus.WouldBlock : PollReadStatus.DataOrEof;
            }

            if (pollRes == 0)
            {
                return owner.HasExited ? PollReadStatus.DataOrEof : PollReadStatus.WouldBlock;
            }

            if ((pfd.Revents & (POLLIN | POLLHUP | POLLERR)) != 0)
            {
                fixed (byte* ptr = buffer)
                {
                    var n = read(fd, ptr, (nuint)buffer.Length);
                    if (n > 0)
                    {
                        bytesRead = (int)n;
                        return PollReadStatus.DataOrEof;
                    }

                    if (n == 0)
                    {
                        return PollReadStatus.DataOrEof;
                    }

                    var readErr = Marshal.GetLastPInvokeError();
                    var eagain = OperatingSystem.IsMacOS() ? EAGAIN_MACOS : EAGAIN_LINUX;
                    if (readErr == EINTR || readErr == eagain)
                    {
                        return PollReadStatus.WouldBlock;
                    }

                    return PollReadStatus.DataOrEof;
                }
            }

            if ((pfd.Revents & POLLNVAL) != 0)
            {
                return PollReadStatus.DataOrEof;
            }

            return PollReadStatus.WouldBlock;
        }

        private static unsafe int TryWriteNonBlocking(PosixPtyProcess owner, ReadOnlySpan<byte> buffer)
        {
            var fd = owner.MasterFd;
            if (fd < 0)
            {
                return -1;
            }

            var pfd = new PollFd
            {
                Fd = fd,
                Events = POLLOUT
            };

            var pollRes = poll(&pfd, 1, 0);
            if (pollRes < 0)
            {
                var err = Marshal.GetLastPInvokeError();
                return err == EINTR ? 0 : -1;
            }

            if (pollRes == 0)
            {
                return owner.HasExited ? -1 : 0;
            }

            if ((pfd.Revents & (POLLHUP | POLLERR | POLLNVAL)) != 0 && (pfd.Revents & POLLOUT) == 0)
            {
                return -1;
            }

            fixed (byte* ptr = buffer)
            {
                var written = write(fd, ptr, (nuint)buffer.Length);
                if (written > 0)
                {
                    return (int)written;
                }

                var err = Marshal.GetLastPInvokeError();
                var eagain = OperatingSystem.IsMacOS() ? EAGAIN_MACOS : EAGAIN_LINUX;
                if (err == EINTR || err == eagain)
                {
                    return 0;
                }

                if (err == EIO || err == EBADF)
                {
                    return -1;
                }

                throw new IOException($"PTY master write failed with errno {err}.");
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr ptsname(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int open(byte* pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint read(int fd, void* buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint write(int fd, void* buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int poll(PollFd* fds, uint nfds, int timeout);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl_vararg(
        int fd,
        nuint request,
        nint argReg,
        nint p3,
        nint p4,
        nint p5,
        nint p6,
        nint p7,
        nint argStack);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl_vararg(
        int fd,
        int cmd,
        int argReg,
        nint p3,
        nint p4,
        nint p5,
        nint p6,
        nint p7,
        nint argStack);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_init(nint file_actions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_destroy(nint file_actions);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int posix_spawn_file_actions_addopen(
        nint file_actions,
        int fildes,
        byte* path,
        int oflag,
        int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_adddup2(nint file_actions, int fildes, int newfildes);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addclose(nint file_actions, int fildes);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int posix_spawn_file_actions_addchdir_np(nint file_actions, byte* path);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_init(nint attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_destroy(nint attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_setflags(nint attr, short flags);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int posix_spawn(
        out int pid,
        byte* path,
        nint file_actions,
        nint attrp,
        byte** argv,
        byte** envp);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int waitpid(int pid, int* status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc")]
    private static extern int isatty(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int tcgetattr(int fd, void* termios_p);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int tcsetattr(int fd, int optional_actions, void* termios_p);

    [DllImport("libc")]
    private static extern unsafe void cfmakeraw(void* termios_p);
}
