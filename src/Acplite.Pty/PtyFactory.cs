namespace Acplite.Pty;

public static class PtyFactory
{
    public static IPtyProcess Start(PtyStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);

        var resolvedFileName = ResolveExecutableOnPath(options.FileName) ?? options.FileName;
        var resolvedOptions = string.Equals(resolvedFileName, options.FileName, StringComparison.Ordinal)
            ? options
            : new PtyStartOptions
            {
                FileName = resolvedFileName,
                Arguments = options.Arguments,
                WorkingDirectory = options.WorkingDirectory,
                Environment = options.Environment,
                InitialSize = options.InitialSize
            };

        if (OperatingSystem.IsWindows())
        {
            return ConPtyProcess.Start(resolvedOptions);
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            return PosixPtyProcess.Start(resolvedOptions);
        }

        throw new PlatformNotSupportedException("Acplite.Pty requires Windows, macOS, or Linux.");
    }

    public static string? ResolveExecutableOnPath(string commandOrPath)
    {
        if (string.IsNullOrWhiteSpace(commandOrPath))
        {
            return null;
        }

        if (Path.IsPathRooted(commandOrPath) ||
            commandOrPath.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            commandOrPath.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return File.Exists(commandOrPath) ? Path.GetFullPath(commandOrPath) : null;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var searchDirs = new List<string>();
        foreach (var part in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            searchDirs.Add(part);
        }

        if (!OperatingSystem.IsWindows())
        {
            var home = PtySessionRegistry.GetUserHomeDirectory();
            searchDirs.Add(Path.Combine(home, ".local", "bin"));
            searchDirs.Add("/opt/homebrew/bin");
            searchDirs.Add("/usr/local/bin");
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? [".exe", ".cmd", ".bat", ".com", ""]
            : [""];

        foreach (var dir in searchDirs)
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, commandOrPath + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static TerminalSize GetControllingTerminalSize()
    {
        if (OperatingSystem.IsWindows())
        {
            return ConPtyProcess.TryGetControllingTerminalSize();
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            return PosixPtyProcess.TryGetControllingTerminalSize();
        }

        return TerminalSize.Default;
    }

    public static IDisposable? TryEnterRawTerminalMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return ConPtyProcess.TryEnterRawTerminalMode();
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            return PosixPtyProcess.TryEnterRawTerminalMode();
        }

        return null;
    }
}
