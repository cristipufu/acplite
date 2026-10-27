using System.Diagnostics;
using System.Text.Json;

namespace Acplite.Pty;

/// <summary>
/// Manages local PTY session registration files in <c>~/.acplite/pty/&lt;pid&gt;.json</c>
/// (or <c>$ACPLITE_PTY_DIR</c>) and prunes stale entries when processes exit.
/// </summary>
public static class PtySessionRegistry
{
    private const long CacheTtlMilliseconds = 300;
    private static volatile CachedSessionsSnapshot? _cachedSnapshot;

    private sealed record CachedSessionsSnapshot(
        string DirectoryPath,
        long TimestampMs,
        IReadOnlyList<PtySessionMetadata> Sessions);

    public static string GetRegistryDirectory(string? customDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(customDirectory))
        {
            return customDirectory;
        }

        var envDir = Environment.GetEnvironmentVariable("ACPLITE_PTY_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            return envDir;
        }

        return Path.Combine(GetUserHomeDirectory(), ".acplite", "pty");
    }

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

    public static string GetMetadataFilePath(int pid, string? registryDirectory = null)
        => Path.Combine(GetRegistryDirectory(registryDirectory), $"{pid}.json");

    public static async Task SaveSessionAsync(
        PtySessionMetadata metadata,
        string? registryDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var dir = GetRegistryDirectory(registryDirectory);
        Directory.CreateDirectory(dir);

        var filePath = GetMetadataFilePath(metadata.Pid, dir);
        var tempPath = $"{filePath}.tmp.{Guid.NewGuid():N}";
        var json = JsonSerializer.Serialize(metadata, PtyJsonContext.Default.PtySessionMetadata);

        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, filePath, overwrite: true);
        _cachedSnapshot = null;
    }

    public static void RemoveSession(int pid, string? registryDirectory = null)
    {
        try
        {
            _cachedSnapshot = null;
            var filePath = GetMetadataFilePath(pid, registryDirectory);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    public static async Task<IReadOnlyList<PtySessionMetadata>> ListActiveSessionsAsync(
        string? registryDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var dir = GetRegistryDirectory(registryDirectory);
        var nowMs = Environment.TickCount64;
        var cached = _cachedSnapshot;
        if (cached is not null &&
            string.Equals(cached.DirectoryPath, dir, StringComparison.Ordinal) &&
            (nowMs - cached.TimestampMs) < CacheTtlMilliseconds)
        {
            return cached.Sessions;
        }

        if (!Directory.Exists(dir))
        {
            _cachedSnapshot = new CachedSessionsSnapshot(dir, nowMs, []);
            return [];
        }

        var active = new List<PtySessionMetadata>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                    var meta = JsonSerializer.Deserialize(json, PtyJsonContext.Default.PtySessionMetadata);
                    if (meta is null)
                    {
                        continue;
                    }

                    if (!IsProcessAlive(meta.Pid) && !IsProcessAlive(meta.ChildPid))
                    {
                        TryDeleteQuietly(file);
                        continue;
                    }

                    active.Add(meta);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Ignore half-written or inaccessible metadata files
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }

        var sorted = active
            .OrderByDescending(static m => m.LastActiveAt)
            .ToList();
        _cachedSnapshot = new CachedSessionsSnapshot(dir, Environment.TickCount64, sorted);
        return sorted;
    }

    public static async Task<PtySessionMetadata?> FindMatchingSessionAsync(
        string providerShortId,
        string? sessionId,
        string? workingDirectory,
        string? registryDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var sessions = await ListActiveSessionsAsync(registryDirectory, cancellationToken).ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            return null;
        }

        // 1. Exact or prefix match by SessionId
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            foreach (var s in sessions)
            {
                if (!string.Equals(s.Provider, providerShortId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(s.SessionId) &&
                    (string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ||
                     s.SessionId.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase) ||
                     sessionId.StartsWith(s.SessionId, StringComparison.OrdinalIgnoreCase)))
                {
                    return s;
                }
            }
        }

        // 2. Match by Provider + normalized WorkingDirectory (preferring unbound or most recently active)
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var normalizedTarget = NormalizePath(workingDirectory);
            PtySessionMetadata? fallbackBound = null;

            foreach (var s in sessions)
            {
                if (!string.Equals(s.Provider, providerShortId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!PathsEqual(NormalizePath(s.WorkingDirectory), normalizedTarget))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(s.SessionId))
                {
                    return s;
                }

                fallbackBound ??= s;
            }

            if (fallbackBound is not null)
            {
                return fallbackBound;
            }
        }

        return null;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return Path.TrimEndingDirectorySeparator(path.Trim());
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(a, b, comparison);
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteQuietly(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // Ignore
        }
    }
}
