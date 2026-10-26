using System.Diagnostics;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;
using Acplite.Sdk.Transport;

namespace Acplite.Sdk.Adapters;

public sealed class CodexAdapter : ProviderAdapterBase
{
    private readonly string _sessionsRootDirectory;

    public CodexAdapter(string? sessionsRootDirectory = null)
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(ProcessTreeStopper.GetUserHomeDirectory(), ".codex");
        }
        _sessionsRootDirectory = sessionsRootDirectory ?? Path.Combine(codexHome, "sessions");
    }

    public override AgentProviderKind Provider => AgentProviderKind.Codex;
    public override string ShortId => "codex";
    public override string DisplayName => "OpenAI Codex";
    public override string DefaultBinaryName => "codex";
    public override bool IsNativeAcp => false;

    public override async Task<IReadOnlyList<AcpDiscoveredSession>> DiscoverSessionsAsync(
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var files = EnumerateRecentJsonlFilesSafely(_sessionsRootDirectory, Math.Max(1, maxResults) * 2);
        var results = new List<AcpDiscoveredSession>(Math.Min(maxResults, files.Count));

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = await CodexTranscriptParser.TryParseSessionFileAsync(file, cancellationToken).ConfigureAwait(false);
            if (parsed is not null)
            {
                results.Add(parsed);
                if (results.Count >= maxResults)
                {
                    break;
                }
            }
        }

        return results;
    }

    protected override IEnumerable<AcpEvent> ParseTranscriptLine(string sessionId, string line)
        => CodexTranscriptParser.ParseTranscriptLine(sessionId, line);

    public override async Task<IAcpSessionHandle> StartSessionAsync(
        AcpSessionConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var binary = ResolveRequiredBinaryPath(config.BinaryPathOverride);

        var args = new List<string> { "app-server" };
        if (config.ExtraArguments is not null)
        {
            args.AddRange(config.ExtraArguments);
        }

        var process = ProcessTreeStopper.StartAgentProcess(binary, args, config.WorkingDirectory, config.ExtraEnvironment);
        var handle = new CodexSessionHandle(
            config,
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            process);

        return await InitializeHandleSafelyAsync(handle, config.HandshakeTimeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<IAcpSessionHandle> StartSessionFromStreamsAsync(
        AcpSessionConfig config,
        Stream agentOutputStream,
        Stream agentInputStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var handle = new CodexSessionHandle(config, agentOutputStream, agentInputStream, process: null);
        return InitializeHandleSafelyAsync(handle, config.HandshakeTimeout, cancellationToken);
    }

    public override async Task<AcpUsageSnapshot> ProbeUsageAsync(
        string? binaryPathOverride = null,
        CancellationToken cancellationToken = default)
    {
        var binary = ProcessTreeStopper.ResolveBinaryPath(binaryPathOverride ?? DefaultBinaryName);
        if (binary is null)
        {
            return new AcpUsageSnapshot(Provider, DateTimeOffset.UtcNow, SummaryText: "Codex not installed");
        }

        Process? process = null;
        AcpJsonRpcTransport? transport = null;
        try
        {
            process = ProcessTreeStopper.StartAgentProcess(binary, ["app-server"], Environment.CurrentDirectory);
            transport = new AcpJsonRpcTransport(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
            transport.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            var initParams = JsonElementBuilder.Build(static w =>
            {
                w.WriteStartObject();
                w.WritePropertyName("clientInfo");
                w.WriteStartObject();
                w.WriteString("name", "acplite");
                w.WriteString("title", "Acplite.Sdk");
                w.WriteString("version", "0.1.0");
                w.WriteEndObject();
                w.WriteEndObject();
            });

            await transport.SendRequestAsync("initialize", initParams, cts.Token).ConfigureAwait(false);
            await transport.SendNotificationAsync("initialized", null, cts.Token).ConfigureAwait(false);

            var rateLimits = await transport.SendRequestAsync("account/rateLimits/read", null, cts.Token).ConfigureAwait(false);
            return CodexTranscriptParser.ParseRateLimitsResponse(rateLimits);
        }
        catch
        {
            return new AcpUsageSnapshot(Provider, DateTimeOffset.UtcNow, PlanType: "Codex", SummaryText: "Codex Ready");
        }
        finally
        {
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            await ProcessTreeStopper.StopProcessTreeAsync(process).ConfigureAwait(false);
        }
    }
}
