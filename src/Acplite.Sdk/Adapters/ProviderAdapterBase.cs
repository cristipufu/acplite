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
        var resolved = ProcessTreeStopper.ResolveBinaryPath(
            binaryPathOverride ?? DefaultBinaryName,
            GetExtraBinaryCandidates());

        var version = resolved is not null
            ? await ProcessTreeStopper.ProbeVersionAsync(resolved, cancellationToken).ConfigureAwait(false)
            : null;

        return new AcpProviderDescriptor(
            Provider: Provider,
            ShortId: ShortId,
            DisplayName: DisplayName,
            BinaryName: binaryPathOverride ?? DefaultBinaryName,
            IsInstalled: resolved is not null,
            ResolvedBinaryPath: resolved,
            Version: version,
            IsNativeAcp: IsNativeAcp,
            SupportsLiveSteering: SupportsLiveSteering,
            SupportsSessionDiscovery: SupportsSessionDiscovery,
            SupportsUsageProbe: SupportsUsageProbe);
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

        await foreach (var line in TranscriptTailer.TailLinesAsync(session.TranscriptPath, includeHistory, followLive, cancellationToken).ConfigureAwait(false))
        {
            foreach (var ev in ParseTranscriptLine(session.SessionId, line))
            {
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
