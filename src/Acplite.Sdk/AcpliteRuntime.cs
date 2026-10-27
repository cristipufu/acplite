using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
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
    private readonly IAcpExternalSessionBridge? _externalBridge;
    private readonly ConcurrentDictionary<IAcpSessionHandle, byte> _activeHandles = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private int _isDisposed;

    public AcpliteRuntime(
        IEnumerable<IAcpProviderAdapter>? customAdapters = null,
        IAcpExternalSessionBridge? externalSessionBridge = null)
    {
        _externalBridge = externalSessionBridge;
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

        var ordered = lists
            .SelectMany(static x => x)
            .OrderByDescending(static s => s.UpdatedAt)
            .ToList();

        if (_externalBridge is null || ordered.Count == 0)
        {
            return ordered;
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            if (!s.IsActive &&
                await _externalBridge.HasActiveSessionAsync(s.Provider, s.SessionId, s.WorkingDirectory, cancellationToken).ConfigureAwait(false))
            {
                ordered[i] = s with { IsActive = true };
            }
        }

        return ordered;
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
        if (!followLive || _externalBridge is null)
        {
            await foreach (var ev in adapter.TailSessionTranscriptAsync(session, includeHistory, followLive, cancellationToken).ConfigureAwait(false))
            {
                yield return ev;
            }
            yield break;
        }

        var merged = Channel.CreateUnbounded<AcpEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var transcriptTask = PumpTranscriptToChannelAsync(adapter, session, includeHistory, merged.Writer, linkedCts.Token);
        var externalTask = PumpExternalToChannelAsync(_externalBridge, session, merged.Writer, linkedCts.Token);
        var completionTask = CompleteWhenBothDoneAsync(transcriptTask, externalTask, merged.Writer);

        try
        {
            await foreach (var ev in merged.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return ev;
            }
        }
        finally
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            try { await completionTask.WaitAsync(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false); } catch { }
        }
    }

    private static async Task CompleteWhenBothDoneAsync(
        Task transcriptTask,
        Task externalTask,
        ChannelWriter<AcpEvent> writer)
    {
        try
        {
            await Task.WhenAll(transcriptTask, externalTask).ConfigureAwait(false);
        }
        catch
        {
            // Individual pumps handle cancellation and errors
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task PumpTranscriptToChannelAsync(
        IAcpProviderAdapter adapter,
        AcpDiscoveredSession session,
        bool includeHistory,
        ChannelWriter<AcpEvent> writer,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await foreach (var ev in adapter.TailSessionTranscriptAsync(session, includeHistory, followLive: true, cancellationToken).ConfigureAwait(false))
            {
                writer.TryWrite(ev);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal cancellation
        }
        catch
        {
            // Keep external channel alive if transcript file is not created yet
        }
    }

    private static async Task PumpExternalToChannelAsync(
        IAcpExternalSessionBridge bridge,
        AcpDiscoveredSession session,
        ChannelWriter<AcpEvent> writer,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await foreach (var ev in bridge.MonitorSessionEventsAsync(session, cancellationToken).ConfigureAwait(false))
            {
                writer.TryWrite(ev);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal cancellation
        }
    }

    public async Task<PtyAttachedSessionHandle?> TryAttachToExternalSessionAsync(
        AcpSessionConfig config,
        AcpDiscoveredSession? discoveredSession = null,
        bool enableInternalTranscriptTail = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        if (_externalBridge is null)
        {
            return null;
        }

        if (discoveredSession is null && !string.IsNullOrWhiteSpace(config.SessionId))
        {
            try
            {
                discoveredSession = await FindSessionByIdAsync(config.SessionId!, config.Provider, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Ignore discovery failure
            }
        }

        var resolvedSessionId = !string.IsNullOrWhiteSpace(config.SessionId)
            ? config.SessionId!
            : Guid.NewGuid().ToString("N");

        var channel = await _externalBridge.TryConnectChannelAsync(
            config.Provider,
            resolvedSessionId,
            config.WorkingDirectory,
            discoveredSession?.TranscriptPath,
            cancellationToken).ConfigureAwait(false);

        if (channel is null)
        {
            return null;
        }

        var adapter = GetAdapter(config.Provider);
        var handle = new PtyAttachedSessionHandle(
            config.Provider,
            resolvedSessionId,
            config.WorkingDirectory,
            channel,
            adapter,
            discoveredSession,
            enableInternalTranscriptTail);

        await RegisterActiveHandleAsync(handle).ConfigureAwait(false);
        return handle;
    }

    public async Task<IAcpSessionHandle> StartSessionAsync(
        AcpSessionConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        if (config.AttachToPtyIfAvailable && (config.ResumeExisting || !string.IsNullOrWhiteSpace(config.SessionId)))
        {
            var ptyHandle = await TryAttachToExternalSessionAsync(config, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (ptyHandle is not null)
            {
                return ptyHandle;
            }
        }

        var adapter = GetAdapter(config.Provider);
        var handle = await adapter.StartSessionAsync(config, cancellationToken).ConfigureAwait(false);
        await RegisterActiveHandleAsync(handle).ConfigureAwait(false);
        return handle;
    }

    private async Task RegisterActiveHandleAsync(IAcpSessionHandle handle)
    {
        _activeHandles[handle] = 0;
        if (handle is AcpSessionHandleBase baseHandle)
        {
            baseHandle.OnDisposed = h => _activeHandles.TryRemove(h, out _);
        }
        else if (handle is PtyAttachedSessionHandle ptyHandle)
        {
            ptyHandle.OnDisposed = h => _activeHandles.TryRemove(h, out _);
        }

        if (Volatile.Read(ref _isDisposed) != 0)
        {
            _activeHandles.TryRemove(handle, out _);
            await handle.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(AcpliteRuntime));
        }
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
