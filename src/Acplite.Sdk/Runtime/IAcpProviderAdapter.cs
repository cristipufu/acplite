using Acplite.Sdk.Protocol;

namespace Acplite.Sdk.Runtime;

public interface IAcpSessionHandle : IAsyncDisposable
{
    AgentProviderKind Provider { get; }
    string SessionId { get; }
    string WorkingDirectory { get; }
    bool SupportsLiveSteering { get; }

    IAsyncEnumerable<AcpEvent> ReadEventsAsync(CancellationToken cancellationToken = default);

    Task SendPromptAsync(string prompt, CancellationToken cancellationToken = default);

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
