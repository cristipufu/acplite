using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;
using Acplite.Sdk.Transport;

namespace Acplite.Sdk.Adapters;

/// <summary>
/// Template base class for ACP provider adapters. Centralizes binary path discovery,
/// version probing, resilient session file enumeration, and leak-free session initialization.
/// </summary>
public abstract class ProviderAdapterBase : IAcpProviderAdapter
{
    private const int MaxCachedSessionFiles = 256;
    private static readonly TimeSpan ActiveSessionWindow = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, AcpProviderDescriptor> _descriptorCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedSessionFileEntry> _sessionFileCache = new(StringComparer.Ordinal);

    private readonly record struct CachedSessionFileEntry(
        long LastWriteTicks,
        long Length,
        AcpDiscoveredSession? Session);

    public abstract AgentProviderKind Provider { get; }
    public abstract string ShortId { get; }
    public abstract string DisplayName { get; }
    public abstract string DefaultBinaryName { get; }
    public abstract bool IsNativeAcp { get; }
    public virtual bool SupportsLiveSteering => true;
    public virtual bool SupportsSessionDiscovery => true;
    public virtual bool SupportsUsageProbe => true;

    protected virtual IReadOnlyList<string> GetExtraBinaryCandidates() => [];

    protected string ResolveRequiredBinaryPath(string? binaryPathOverride)
    {
        var targetName = binaryPathOverride ?? DefaultBinaryName;
        return ProcessTreeStopper.ResolveBinaryPath(targetName, GetExtraBinaryCandidates())
            ?? throw new FileNotFoundException($"Could not find '{targetName}' CLI executable on PATH.");
    }

    public virtual async Task<AcpProviderDescriptor> DiscoverInstalledAsync(
        string? binaryPathOverride = null,
        CancellationToken cancellationToken = default)
    {
        var binaryName = binaryPathOverride ?? DefaultBinaryName;
        var resolved = ProcessTreeStopper.ResolveBinaryPath(
            binaryName,
            GetExtraBinaryCandidates());

        var cacheKey = $"{binaryName}|{resolved ?? string.Empty}";
        if (_descriptorCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var version = resolved is not null
            ? await ProcessTreeStopper.ProbeVersionAsync(resolved, cancellationToken).ConfigureAwait(false)
            : null;

        var descriptor = new AcpProviderDescriptor(
            Provider: Provider,
            ShortId: ShortId,
            DisplayName: DisplayName,
            BinaryName: binaryName,
            IsInstalled: resolved is not null,
            ResolvedBinaryPath: resolved,
            Version: version,
            IsNativeAcp: IsNativeAcp,
            SupportsLiveSteering: SupportsLiveSteering,
            SupportsSessionDiscovery: SupportsSessionDiscovery,
            SupportsUsageProbe: SupportsUsageProbe);

        _descriptorCache[cacheKey] = descriptor;
        return descriptor;
    }

    protected async ValueTask<AcpDiscoveredSession?> GetOrParseSessionFileAsync(
        FileInfo file,
        Func<FileInfo, CancellationToken, Task<AcpDiscoveredSession?>> parser,
        CancellationToken cancellationToken)
    {
        var path = file.FullName;
        var writeTicks = file.LastWriteTimeUtc.Ticks;
        var length = file.Length;

        if (_sessionFileCache.TryGetValue(path, out var entry) &&
            entry.LastWriteTicks == writeTicks &&
            entry.Length == length)
        {
            if (entry.Session is null)
            {
                return null;
            }

            var isActiveNow = (DateTime.UtcNow - file.LastWriteTimeUtc) < ActiveSessionWindow;
            return entry.Session.IsActive == isActiveNow
                ? entry.Session
                : entry.Session with { IsActive = isActiveNow };
        }

        var parsed = await parser(file, cancellationToken).ConfigureAwait(false);
        if (_sessionFileCache.Count >= MaxCachedSessionFiles)
        {
            _sessionFileCache.Clear();
        }
        _sessionFileCache[path] = new CachedSessionFileEntry(writeTicks, length, parsed);
        return parsed;
    }

    public abstract Task<IReadOnlyList<AcpDiscoveredSession>> DiscoverSessionsAsync(
        int maxResults = 50,
        CancellationToken cancellationToken = default);

    public async IAsyncEnumerable<AcpEvent> TailSessionTranscriptAsync(
        AcpDiscoveredSession session,
        bool includeHistory = true,
        bool followLive = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!SupportsSessionDiscovery || string.IsNullOrWhiteSpace(session.TranscriptPath))
        {
            yield break;
        }

        AcpTranscriptHistoryEntryEvent? lastHistoryEvent = null;

        await foreach (var line in TranscriptTailer.TailLinesAsync(session.TranscriptPath, includeHistory, followLive, cancellationToken).ConfigureAwait(false))
        {
            foreach (var ev in ParseTranscriptLine(session.SessionId, line))
            {
                if (ev is AcpTranscriptHistoryEntryEvent hist)
                {
                    if (lastHistoryEvent is not null &&
                        string.Equals(lastHistoryEvent.Role, hist.Role, StringComparison.Ordinal) &&
                        string.Equals(lastHistoryEvent.Text, hist.Text, StringComparison.Ordinal) &&
                        string.Equals(lastHistoryEvent.Thinking, hist.Thinking, StringComparison.Ordinal) &&
                        string.Equals(lastHistoryEvent.ToolName, hist.ToolName, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    lastHistoryEvent = hist;
                }

                yield return ev;
            }
        }
    }

    protected virtual IEnumerable<AcpEvent> ParseTranscriptLine(string sessionId, string line)
        => [];

    public abstract Task<IAcpSessionHandle> StartSessionAsync(
        AcpSessionConfig config,
        CancellationToken cancellationToken = default);

    public abstract Task<AcpUsageSnapshot> ProbeUsageAsync(
        string? binaryPathOverride = null,
        CancellationToken cancellationToken = default);

    protected static async Task<IAcpSessionHandle> InitializeHandleSafelyAsync(
        AcpSessionHandleBase handle,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeCts.CancelAfter(handshakeTimeout);

        try
        {
            await handle.InitializeAsync(handshakeCts.Token).ConfigureAwait(false);
            return handle;
        }
        catch
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    protected static IReadOnlyList<FileInfo> EnumerateRecentJsonlFilesSafely(
        string rootDirectory,
        int maxFilesToInspect,
        Func<FileInfo, bool>? predicate = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return [];
        }

        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false
            };

            var query = new DirectoryInfo(rootDirectory).EnumerateFiles("*.jsonl", options);
            if (predicate is not null)
            {
                query = query.Where(predicate);
            }

            return query
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .Take(Math.Max(1, maxFilesToInspect))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
