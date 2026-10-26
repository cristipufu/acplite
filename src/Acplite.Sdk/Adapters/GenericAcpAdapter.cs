using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;
using Acplite.Sdk.Transport;

namespace Acplite.Sdk.Adapters;

public sealed class GenericAcpAdapter : ProviderAdapterBase
{
    public override AgentProviderKind Provider => AgentProviderKind.GenericAcp;
    public override string ShortId => "acp";
    public override string DisplayName => "Generic ACP";
    public override string DefaultBinaryName => "acp-agent";
    public override bool IsNativeAcp => true;
    public override bool SupportsSessionDiscovery => false;
    public override bool SupportsUsageProbe => false;

    public override Task<IReadOnlyList<AcpDiscoveredSession>> DiscoverSessionsAsync(
        int maxResults = 50,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AcpDiscoveredSession>>([]);

    public override async Task<IAcpSessionHandle> StartSessionAsync(
        AcpSessionConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var binary = ResolveRequiredBinaryPath(config.BinaryPathOverride);
        var args = config.ExtraArguments ?? [];

        var process = ProcessTreeStopper.StartAgentProcess(binary, args, config.WorkingDirectory, config.ExtraEnvironment);
        var handle = new NativeAcpSessionHandle(
            Provider,
            config,
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            process,
            preferXaiExtensions: false);

        return await InitializeHandleSafelyAsync(handle, config.HandshakeTimeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<IAcpSessionHandle> StartSessionFromStreamsAsync(
        AcpSessionConfig config,
        Stream agentOutputStream,
        Stream agentInputStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var handle = new NativeAcpSessionHandle(
            Provider,
            config,
            agentOutputStream,
            agentInputStream,
            process: null,
            preferXaiExtensions: false);

        return InitializeHandleSafelyAsync(handle, config.HandshakeTimeout, cancellationToken);
    }

    public override Task<AcpUsageSnapshot> ProbeUsageAsync(
        string? binaryPathOverride = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new AcpUsageSnapshot(Provider, DateTimeOffset.UtcNow, SummaryText: "Generic ACP"));
}
