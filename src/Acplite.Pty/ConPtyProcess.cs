using System.Collections;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Acplite.Pty;

[SupportedOSPlatform("windows")]
internal sealed class ConPtyProcess : IPtyProcess
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const nint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_ECHO_INPUT = 0x0004;
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public nint lpAttributeList;
    }

    private readonly int _processId;
    private readonly NamedPipeServerStream _inputStream;
    private readonly NamedPipeServerStream _outputStream;
    private readonly TaskCompletionSource<int> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEvent _processWaitEvent;
    private readonly RegisteredWaitHandle _registeredWait;
    private nint _hPC;
    private nint _hProcess;
    private nint _hThread;
    private int _hasExited;
    private int _isDisposed;

    private ConPtyProcess(
        int processId,
        nint hPC,
        nint hProcess,
        nint hThread,
        NamedPipeServerStream inputStream,
        NamedPipeServerStream outputStream)
    {
        _processId = processId;
        _hPC = hPC;
        _hProcess = hProcess;
        _hThread = hThread;
        _inputStream = inputStream;
        _outputStream = outputStream;

        _processWaitEvent = new ManualResetEvent(false)
        {
            SafeWaitHandle = new SafeWaitHandle(hProcess, ownsHandle: false)
        };

        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _processWaitEvent,
            static (state, _) => ((ConPtyProcess)state!).OnProcessSignaled(),
            this,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: true);
    }

    public int ProcessId => _processId;
    public bool HasExited => Volatile.Read(ref _hasExited) != 0;
    public Stream InputStream => _inputStream;
    public Stream OutputStream => _outputStream;

    public static unsafe ConPtyProcess Start(PtyStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);

        var inPipeName = $"acplite-conpty-in-{Guid.NewGuid():N}";
        var outPipeName = $"acplite-conpty-out-{Guid.NewGuid():N}";

        NamedPipeServerStream? ptyInputWrite = null;
        NamedPipeClientStream? ptyInputRead = null;
        NamedPipeServerStream? ptyOutputRead = null;
        NamedPipeClientStream? ptyOutputWrite = null;
        nint hPC = 0;

        try
        {
            // Parent streams use PipeOptions.Asynchronous (FILE_FLAG_OVERLAPPED) for true IOCP async I/O.
            ptyInputWrite = new NamedPipeServerStream(
                inPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            ptyInputRead = new NamedPipeClientStream(".", inPipeName, PipeDirection.In, PipeOptions.None);
            ptyInputRead.Connect(2000);
            ptyInputWrite.WaitForConnection();

            ptyOutputRead = new NamedPipeServerStream(
                outPipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            ptyOutputWrite = new NamedPipeClientStream(".", outPipeName, PipeDirection.Out, PipeOptions.None);
            ptyOutputWrite.Connect(2000);
            ptyOutputRead.WaitForConnection();

            var clamped = options.InitialSize.Clamp();
            var size = new COORD { X = (short)clamped.Columns, Y = (short)clamped.Rows };
            var hr = CreatePseudoConsole(
                size,
                ptyInputRead.SafePipeHandle.DangerousGetHandle(),
                ptyOutputWrite.SafePipeHandle.DangerousGetHandle(),
                0,
                out hPC);

            if (hr != 0 || hPC == 0)
            {
                throw new InvalidOperationException($"CreatePseudoConsole failed with HRESULT 0x{hr:X8}.");
            }
        }
        catch
        {
            ptyInputWrite?.Dispose();
            ptyOutputRead?.Dispose();
            throw;
        }
        finally
        {
            // ConPTY duplicates the child-side handles; always dispose our client copies.
            ptyInputRead?.Dispose();
            ptyOutputWrite?.Dispose();
        }

        nint attrList = 0;
        try
        {
            nuint attrListSize = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, &attrListSize);
            attrList = (nint)NativeMemory.AllocZeroed(attrListSize);

            if (!InitializeProcThreadAttributeList(attrList, 1, 0, &attrListSize))
            {
                throw new InvalidOperationException($"InitializeProcThreadAttributeList failed with error {Marshal.GetLastPInvokeError()}.");
            }

            if (!UpdateProcThreadAttribute(
                    attrList,
                    0,
                    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hPC,
                    (nuint)sizeof(nint),
                    0,
                    0))
            {
                throw new InvalidOperationException($"UpdateProcThreadAttribute failed with error {Marshal.GetLastPInvokeError()}.");
            }

            var startupInfo = new STARTUPINFOEXW
            {
                StartupInfo = new STARTUPINFOW { cb = sizeof(STARTUPINFOEXW) },
                lpAttributeList = attrList
            };

            var commandLine = BuildCommandLine(options.FileName, options.Arguments);
            var envBlock = BuildUnicodeEnvironmentBlock(options.Environment);
            var cwd = Directory.Exists(options.WorkingDirectory)
                ? options.WorkingDirectory
                : Environment.CurrentDirectory;

            fixed (char* cmdPtr = commandLine)
            fixed (char* envPtr = envBlock)
            fixed (char* cwdPtr = cwd)
            {
                if (!CreateProcessW(
                        null,
                        cmdPtr,
                        0,
                        0,
                        false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                        envPtr,
                        cwdPtr,
                        &startupInfo,
                        out var pi))
                {
                    throw new InvalidOperationException($"CreateProcessW failed with error {Marshal.GetLastPInvokeError()}.");
                }

                return new ConPtyProcess((int)pi.dwProcessId, hPC, pi.hProcess, pi.hThread, ptyInputWrite, ptyOutputRead);
            }
        }
        catch
        {
            ClosePseudoConsole(hPC);
            ptyInputWrite.Dispose();
            ptyOutputRead.Dispose();
            throw;
        }
        finally
        {
            var attrListToFree = Interlocked.Exchange(ref attrList, 0);
            if (attrListToFree != 0)
            {
                FreeAttributeList(attrListToFree);
            }
        }
    }

    public void Resize(TerminalSize size)
    {
        var hPC = Volatile.Read(ref _hPC);
        if (hPC == 0 || HasExited)
        {
            return;
        }

        var clamped = size.Clamp();
        var coord = new COORD { X = (short)clamped.Columns, Y = (short)clamped.Rows };
        _ = ResizePseudoConsole(hPC, coord);
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        => await _exitTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

    public void Kill()
    {
        var hProc = Volatile.Read(ref _hProcess);
        if (hProc != 0 && !HasExited)
        {
            _ = TerminateProcess(hProc, 1);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        try { await _inputStream.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await _outputStream.DisposeAsync().ConfigureAwait(false); } catch { }

        var hPC = Interlocked.Exchange(ref _hPC, 0);
        if (hPC != 0)
        {
            ClosePseudoConsole(hPC);
        }

        if (!HasExited)
        {
            Kill();
        }

        try
        {
            await _exitTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore timeout
        }

        try { _registeredWait.Unregister(null); } catch { }
        try { _processWaitEvent.Dispose(); } catch { }

        var hThread = Interlocked.Exchange(ref _hThread, 0);
        if (hThread != 0)
        {
            _ = CloseHandle(hThread);
        }

        var hProc = Interlocked.Exchange(ref _hProcess, 0);
        if (hProc != 0)
        {
            _ = CloseHandle(hProc);
        }
    }

    private void OnProcessSignaled()
    {
        var hProc = Volatile.Read(ref _hProcess);
        uint exitCode = 0;
        if (hProc != 0)
        {
            _ = GetExitCodeProcess(hProc, out exitCode);
        }

        Volatile.Write(ref _hasExited, 1);
        _exitTcs.TrySetResult((int)exitCode);
    }

    private static unsafe void FreeAttributeList(nint attrList)
    {
        DeleteProcThreadAttributeList(attrList);
        NativeMemory.Free((void*)attrList);
    }

    private static string BuildCommandLine(string fileName, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        var isCmdScript = fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                          fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        if (isCmdScript)
        {
            var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            AppendQuotedArg(sb, comSpec);
            sb.Append(" /d /c ");
        }

        AppendQuotedArg(sb, fileName);
        foreach (var arg in args)
        {
            sb.Append(' ');
            AppendQuotedArg(sb, arg);
        }

        return sb.ToString();
    }

    private static void AppendQuotedArg(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && !arg.AsSpan().ContainsAny(" \t\n\v\"".AsSpan()))
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        for (var i = 0; i < arg.Length;)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }

            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                i++;
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(arg[i]);
                i++;
            }
        }
        sb.Append('"');
    }

    private static string BuildUnicodeEnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
    {
        var dict = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v)
            {
                dict[k] = v;
            }
        }

        dict["ACPLITE_PTY_ACTIVE"] = "1";

        if (overrides is not null)
        {
            foreach (var (k, v) in overrides)
            {
                dict[k] = v;
            }
        }

        var sb = new StringBuilder();
        foreach (var (k, v) in dict)
        {
            sb.Append(k).Append('=').Append(v).Append('\0');
        }
        sb.Append('\0');
        return sb.ToString();
    }

    public static TerminalSize TryGetControllingTerminalSize()
    {
        var hOut = GetStdHandle(STD_OUTPUT_HANDLE);
        if (hOut != 0 && hOut != -1 && GetConsoleScreenBufferInfo(hOut, out var info))
        {
            var cols = info.srWindow.Right - info.srWindow.Left + 1;
            var rows = info.srWindow.Bottom - info.srWindow.Top + 1;
            if (cols > 0 && rows > 0)
            {
                return new TerminalSize(cols, rows).Clamp();
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

    public static IDisposable? TryEnterRawTerminalMode()
    {
        var hIn = GetStdHandle(STD_INPUT_HANDLE);
        var hOut = GetStdHandle(STD_OUTPUT_HANDLE);
        if (hIn == 0 || hIn == -1 || !GetConsoleMode(hIn, out var origInMode))
        {
            return null;
        }

        uint origOutMode = 0;
        var hasOutMode = hOut != 0 && hOut != -1 && GetConsoleMode(hOut, out origOutMode);
        var newInMode = (origInMode & ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT)) | ENABLE_VIRTUAL_TERMINAL_INPUT;
        _ = SetConsoleMode(hIn, newInMode);

        if (hasOutMode)
        {
            _ = SetConsoleMode(hOut, origOutMode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN);
        }

        return new WindowsConsoleModeRestorer(hIn, origInMode, hasOutMode ? hOut : 0, origOutMode);
    }

    private sealed class WindowsConsoleModeRestorer(nint hIn, uint origInMode, nint hOut, uint origOutMode) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _ = SetConsoleMode(hIn, origInMode);
            if (hOut != 0)
            {
                _ = SetConsoleMode(hOut, origOutMode);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        COORD size,
        nint hInput,
        nint hOutput,
        uint dwFlags,
        out nint phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(nint hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(nint hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool InitializeProcThreadAttributeList(
        nint lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        nuint* lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        nint lpAttributeList,
        uint dwFlags,
        nint attribute,
        nint lpValue,
        nuint cbSize,
        nint lpPreviousValue,
        nint lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(nint lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern unsafe bool CreateProcessW(
        char* lpApplicationName,
        char* lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        char* lpEnvironment,
        char* lpCurrentDirectory,
        STARTUPINFOEXW* lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(nint hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(nint hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);
}
