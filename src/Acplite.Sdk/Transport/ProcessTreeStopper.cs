using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Acplite.Sdk.Transport;

/// <summary>
/// Cross-platform binary discovery (Windows, macOS, Linux), process spawning with
/// sanitized UTF-8 streams, and descendant process-tree termination.
/// </summary>
public static class ProcessTreeStopper
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly string[] StrippedNestedVariables =
    [
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_SSE_PORT",
        "MallocStackLogging",
        "MallocStackLoggingNoCompact",
        "MallocScribble"
    ];

    static ProcessTreeStopper()
    {
        SanitizeCurrentProcessEnvironment();
    }

    public static void SanitizeCurrentProcessEnvironment()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        try
        {
            Environment.SetEnvironmentVariable("MallocStackLogging", null);
            Environment.SetEnvironmentVariable("MallocStackLoggingNoCompact", null);
            Environment.SetEnvironmentVariable("MallocScribble", null);
        }
        catch
        {
            // Ignore if environment is read-only
        }
    }

    private static readonly string[] WindowsExecutableExtensions = [".exe", ".cmd", ".bat", ""];
    private static readonly string[] UnixExecutableExtensions = [""];

    public static string GetUserHomeDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            return profile;
        }

        var homeEnv = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(homeEnv))
        {
            return homeEnv;
        }

        return Environment.CurrentDirectory;
    }

    public static string? ResolveBinaryPath(string binaryName, IReadOnlyList<string>? extraCandidatePaths = null)
    {
        if (string.IsNullOrWhiteSpace(binaryName))
        {
            return null;
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var extensions = isWindows ? WindowsExecutableExtensions : UnixExecutableExtensions;

        if (Path.IsPathRooted(binaryName))
        {
            var matchedRooted = TryMatchFileWithExtensions(binaryName, extensions);
            if (matchedRooted is not null)
            {
                return matchedRooted;
            }
        }

        if (extraCandidatePaths is not null)
        {
            foreach (var candidate in extraCandidatePaths)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var matchedCandidate = TryMatchFileWithExtensions(candidate, extensions);
                if (matchedCandidate is not null)
                {
                    return matchedCandidate;
                }
            }
        }

        foreach (var dir in GetCrossPlatformSearchDirectories(isWindows))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var matched = TryMatchFileWithExtensions(Path.Combine(dir, binaryName), extensions);
            if (matched is not null)
            {
                return matched;
            }
        }

        return null;
    }

    private static string? TryMatchFileWithExtensions(string basePath, string[] extensions)
    {
        if (Path.HasExtension(basePath) && File.Exists(basePath))
        {
            return basePath;
        }

        foreach (var ext in extensions)
        {
            var candidate = string.IsNullOrEmpty(ext) ? basePath : basePath + ext;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static List<string> GetCrossPlatformSearchDirectories(bool isWindows)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathDirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var searchDirs = new List<string>(pathDirs);

        var home = GetUserHomeDirectory();
        if (!string.IsNullOrEmpty(home))
        {
            searchDirs.Add(Path.Combine(home, ".antigravity", "bin"));
            searchDirs.Add(Path.Combine(home, ".local", "bin"));
            searchDirs.Add(Path.Combine(home, ".claude", "local"));
            searchDirs.Add(Path.Combine(home, ".volta", "bin"));
            searchDirs.Add(Path.Combine(home, ".bun", "bin"));
            searchDirs.Add(Path.Combine(home, ".cargo", "bin"));
        }

        if (isWindows)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                searchDirs.Add(Path.Combine(appData, "npm"));
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                searchDirs.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));
                searchDirs.Add(Path.Combine(localAppData, "Programs", "Antigravity", "bin"));
                searchDirs.Add(Path.Combine(localAppData, "AnthropicClaude"));
                searchDirs.Add(Path.Combine(localAppData, "pnpm"));
            }

            if (!string.IsNullOrEmpty(home))
            {
                searchDirs.Add(Path.Combine(home, "scoop", "shims"));
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(home))
            {
                searchDirs.Add(Path.Combine(home, ".npm-global", "bin"));
                searchDirs.Add(Path.Combine(home, ".local", "share", "pnpm"));
            }

            searchDirs.Add("/opt/homebrew/bin");
            searchDirs.Add("/usr/local/bin");
            searchDirs.Add("/home/linuxbrew/.linuxbrew/bin");
            searchDirs.Add("/snap/bin");
        }

        return searchDirs;
    }

    public static async Task<string?> ProbeVersionAsync(string binaryPath, CancellationToken cancellationToken = default)
    {
        Process? process = null;
        try
        {
            var psi = CreateCrossPlatformStartInfo(binaryPath, ["--version"], Environment.CurrentDirectory);

            process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // Ignore if already closed
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var combined = (!string.IsNullOrWhiteSpace(stdout) ? stdout : stderr).Trim();
            if (string.IsNullOrEmpty(combined))
            {
                return null;
            }

            var firstLine = combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            return firstLine.Length > 80 ? firstLine[..80] : firstLine;
        }
        catch
        {
            return null;
        }
        finally
        {
            await StopProcessTreeAsync(process).ConfigureAwait(false);
        }
    }

    public static Process StartAgentProcess(
        string binaryPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var psi = CreateCrossPlatformStartInfo(binaryPath, arguments, workingDirectory);

        foreach (var key in StrippedNestedVariables)
        {
            psi.Environment.Remove(key);
        }

        if (extraEnvironment is not null)
        {
            foreach (var (k, v) in extraEnvironment)
            {
                psi.Environment[k] = v;
            }
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start agent process '{binaryPath}'.");
        return process;
    }

    private static ProcessStartInfo CreateCrossPlatformStartInfo(
        string binaryPath,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var isWindowsCmdWrapper = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            (binaryPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             binaryPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        var psi = new ProcessStartInfo
        {
            FileName = isWindowsCmdWrapper
                ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                : binaryPath,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (isWindowsCmdWrapper)
        {
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(binaryPath);
        }

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var key in StrippedNestedVariables)
        {
            psi.Environment.Remove(key);
        }

        return psi;
    }

    public static async Task StopProcessTreeAsync(Process? process, TimeSpan? gracePeriod = null)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // Ignore if already closed
            }

            var grace = gracePeriod ?? TimeSpan.FromMilliseconds(400);
            using var cts = new CancellationTokenSource(grace);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                // Proceed to forceful tree kill
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await process.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
            }
        }
        catch
        {
            // Process already exited or inaccessible
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                // Ignore
            }
        }
    }
}
