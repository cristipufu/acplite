using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Acplite.Sdk.Adapters;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;

namespace Acplite.Sdk;

/// <summary>
/// Unified entry point for discovering coding agents (<c>claude</c>, <c>codex</c>, <c>grok</c>, <c>agy</c>),
/// listing and live-tailing local sessions on disk, starting or resuming interactive ACP sessions,
/// and probing account usage quotas.
/// </summary>
public sealed class AcpliteRuntime : IAsyncDisposable
{
    private readonly Dictionary<AgentProviderKind, IAcpProviderAdapter> _adapters;
    private readonly ConcurrentDictionary<IAcpSessionHandle, byte> _activeHandles = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private int _isDisposed;

    public AcpliteRuntime(IEnumerable<IAcpProviderAdapter>? customAdapters = null)
    {
        _adapters = new Dictionary<AgentProviderKind, IAcpProviderAdapter>
        {
            [AgentProviderKind.Claude] = new ClaudeCodeAdapter(),
            [AgentProviderKind.Codex] = new CodexAdapter(),
            [AgentProviderKind.Grok] = new GrokAdapter(),
            [AgentProviderKind.Antigravity] = new AntigravityAdapter(),
            [AgentProviderKind.GenericAcp] = new GenericAcpAdapter()
        };

        if (customAdapters is not null)
        {
            foreach (var adapter in customAdapters)
            {
                _adapters[adapter.Provider] = adapter;
            }
        }
    }

    public int ActiveSessionCount => _activeHandles.Count;

    public IAcpProviderAdapter GetAdapter(AgentProviderKind provider)
        => _adapters.TryGetValue(provider, out var adapter)
            ? adapter
            : throw new ArgumentOutOfRangeException(nameof(provider), $"No ACP adapter registered for '{provider}'.");

    public async Task<IReadOnlyList<AcpProviderDescriptor>> DiscoverProvidersAsync(
        AgentProviderKind? providerFilter = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        var targets = providerFilter.HasValue
            ? [GetAdapter(providerFilter.Value)]
            : _adapters.Values.Where(static a => a.Provider != AgentProviderKind.GenericAcp).ToArray();

        var tasks = targets.Select(a => a.DiscoverInstalledAsync(cancellationToken: cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AcpDiscoveredSession>> DiscoverSessionsAsync(
        AgentProviderKind? providerFilter = null,
        int maxSessionsPerProvider = 25,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        var targets = providerFilter.HasValue
            ? [GetAdapter(providerFilter.Value)]
            : _adapters.Values.Where(static a => a.SupportsSessionDiscovery).ToArray();

        var tasks = targets.Select(a => a.DiscoverSessionsAsync(maxSessionsPerProvider, cancellationToken));
        var lists = await Task.WhenAll(tasks).ConfigureAwait(false);

        return lists
            .SelectMany(static x => x)
            .OrderByDescending(static s => s.UpdatedAt)
            .ToList();
    }

    public async Task<AcpDiscoveredSession?> FindSessionByIdAsync(
        string sessionId,
        AgentProviderKind? providerFilter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        var sessions = await DiscoverSessionsAsync(providerFilter, maxSessionsPerProvider: 100, cancellationToken).ConfigureAwait(false);
        return sessions.FirstOrDefault(s =>
            string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ||
            s.SessionId.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase));
    }

    public async IAsyncEnumerable<AcpEvent> TailSessionAsync(
        AcpDiscoveredSession session,
        bool includeHistory = true,
        bool followLive = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        var adapter = GetAdapter(session.Provider);
        await foreach (var ev in adapter.TailSessionTranscriptAsync(session, includeHistory, followLive, cancellationToken).ConfigureAwait(false))
        {
            yield return ev;
        }
    }

    public async Task<IAcpSessionHandle> StartSessionAsync(
        AcpSessionConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        var adapter = GetAdapter(config.Provider);
        var handle = await adapter.StartSessionAsync(config, cancellationToken).ConfigureAwait(false);

        _activeHandles[handle] = 0;
        if (handle is AcpSessionHandleBase baseHandle)
        {
            baseHandle.OnDisposed = h => _activeHandles.TryRemove(h, out _);
        }

        if (Volatile.Read(ref _isDisposed) != 0)
        {
            _activeHandles.TryRemove(handle, out _);
            await handle.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(AcpliteRuntime));
        }

        return handle;
    }

    public async Task<IReadOnlyList<AcpUsageSnapshot>> ProbeUsageAsync(
        AgentProviderKind? providerFilter = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        var targets = providerFilter.HasValue
            ? [GetAdapter(providerFilter.Value)]
            : _adapters.Values.Where(static a => a.SupportsUsageProbe).ToArray();

        var tasks = targets.Select(a => a.ProbeUsageAsync(cancellationToken: cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
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

            foreach (var (handle, _) in _activeHandles)
            {
                if (_activeHandles.TryRemove(handle, out _))
                {
                    await handle.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _disposeGate.Release();
        }
    }
}
