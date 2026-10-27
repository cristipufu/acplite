namespace Acplite.Pty;

/// <summary>
/// Represents an active child process attached to a pseudo-terminal (POSIX PTY on macOS/Linux
/// or ConPTY on Windows).
/// </summary>
public interface IPtyProcess : IAsyncDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    Stream InputStream { get; }
    Stream OutputStream { get; }
    void Resize(TerminalSize size);
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
    void Kill();
}
