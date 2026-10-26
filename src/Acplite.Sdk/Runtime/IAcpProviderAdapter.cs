using Acplite.Sdk.Protocol;

namespace Acplite.Sdk.Runtime;

public interface IAcpSessionHandle : IAsyncDisposable
{
    AgentProviderKind Provider { get; }
    string SessionId { get; }
    string WorkingDirectory { get; }
    bool SupportsLiveSteering { get; }

    /// <summary>
    /// Gets whether an agent turn is currently executing on this session.
    /// </summary>
    bool IsTurnActive { get; }

    IAsyncEnumerable<AcpEvent> ReadEventsAsync(CancellationToken cancellationToken = default);

    Task SendPromptAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues a prompt for the next turn if a turn is currently active, or dispatches it
    /// immediately via <see cref="SendPromptAsync"/> if the session is idle.
    /// Returns the queued item when queued, or <see langword="null"/> when dispatched immediately.
    /// </summary>
    Task<AcpQueuedPromptItem?> EnqueuePromptAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a snapshot of prompts currently queued for subsequent turns.
    /// </summary>
    IReadOnlyList<AcpQueuedPromptItem> GetQueuedPrompts();

    /// <summary>
    /// Removes a pending prompt from the next-turn queue by its <paramref name="queueItemId"/>.
    /// </summary>
    Task<bool> RemoveQueuedPromptAsync(string queueItemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all pending prompts from the next-turn queue.
    /// </summary>
    Task ClearQueuedPromptsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Interrupts the currently executing turn (if active) and immediately dispatches <paramref name="prompt"/> as a new turn.
    /// </summary>
    Task InterruptAndSendPromptAsync(string prompt, CancellationToken cancellationToken = default);

    Task<bool> SteerAsync(string steeringPrompt, CancellationToken cancellationToken = default);

    Task RespondPermissionAsync(
        string requestId,
        bool approved,
        string? selectedOptionId = null,
        CancellationToken cancellationToken = default);

    Task SetModelAsync(string modelId, CancellationToken cancellationToken = default);

    Task CancelTurnAsync(CancellationToken cancellationToken = default);
}

public interface IAcpProviderAdapter
{
    AgentProviderKind Provider { get; }
    string ShortId { get; }
    string DisplayName { get; }
    string DefaultBinaryName { get; }
    bool IsNativeAcp { get; }
    bool SupportsLiveSteering { get; }
    bool SupportsSessionDiscovery { get; }
    bool SupportsUsageProbe { get; }

    Task<AcpProviderDescriptor> DiscoverInstalledAsync(
        string? binaryPathOverride = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AcpDiscoveredSession>> DiscoverSessionsAsync(
        int maxResults = 50,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AcpEvent> TailSessionTranscriptAsync(
        AcpDiscoveredSession session,
        bool includeHistory = true,
        bool followLive = true,
        CancellationToken cancellationToken = default);

    Task<IAcpSessionHandle> StartSessionAsync(
        AcpSessionConfig config,
        CancellationToken cancellationToken = default);

    Task<AcpUsageSnapshot> ProbeUsageAsync(
        string? binaryPathOverride = null,
        CancellationToken cancellationToken = default);
}
