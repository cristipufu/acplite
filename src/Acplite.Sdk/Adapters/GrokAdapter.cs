using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Acplite.Sdk.Formatting;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;
using Acplite.Sdk.Transport;

namespace Acplite.Sdk.Adapters;

public sealed class GrokAdapter : ProviderAdapterBase
{
    private readonly string _sessionsRootDirectory;

    public GrokAdapter(string? sessionsRootDirectory = null)
    {
        var grokHome = Environment.GetEnvironmentVariable("GROK_HOME");
        if (string.IsNullOrWhiteSpace(grokHome))
        {
            grokHome = Path.Combine(ProcessTreeStopper.GetUserHomeDirectory(), ".grok");
        }
        _sessionsRootDirectory = sessionsRootDirectory ?? Path.Combine(grokHome, "sessions");
    }

    public override AgentProviderKind Provider => AgentProviderKind.Grok;
    public override string ShortId => "grok";
    public override string DisplayName => "xAI Grok";
    public override string DefaultBinaryName => "grok";
    public override bool IsNativeAcp => true;

    public override async Task<IReadOnlyList<AcpDiscoveredSession>> DiscoverSessionsAsync(
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var files = EnumerateRecentJsonlFilesSafely(_sessionsRootDirectory, Math.Max(1, maxResults));
        var results = new List<AcpDiscoveredSession>(files.Count);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = Path.GetFileNameWithoutExtension(file.Name);
            string? title = null;
            var msgCount = 0;

            try
            {
                await using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while (msgCount < 20 && (line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;
                    using var doc = JsonDocument.Parse(line);
                    msgCount++;
                    title ??= doc.RootElement.GetOptionalString("text");
                }
            }
            catch
            {
                // Ignore unreadable file
            }

            var displayTitle = !string.IsNullOrWhiteSpace(title)
                ? TextSanitizer.TruncateSingleLine(title)
                : $"Grok session {sessionId[..Math.Min(8, sessionId.Length)]}";

            results.Add(new AcpDiscoveredSession(
                Provider: Provider,
                SessionId: sessionId,
                Title: displayTitle,
                WorkingDirectory: null,
                TranscriptPath: file.FullName,
                UpdatedAt: new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                CreatedAt: new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
                Model: "grok-4",
                IsActive: (DateTime.UtcNow - file.LastWriteTimeUtc) < TimeSpan.FromMinutes(2),
                MessageCount: msgCount));
        }

        return results;
    }

    protected override IEnumerable<AcpEvent> ParseTranscriptLine(string sessionId, string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            yield break;
        }

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch
        {
            yield break;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var role = root.GetStringOrDefault("role", "assistant");
            var text = root.GetOptionalString("text");
            if (!string.IsNullOrEmpty(text))
            {
                yield return new AcpTranscriptHistoryEntryEvent(Provider, sessionId, DateTimeOffset.UtcNow, role, text);
            }
        }
    }

    public override async Task<IAcpSessionHandle> StartSessionAsync(
        AcpSessionConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var binary = ResolveRequiredBinaryPath(config.BinaryPathOverride);

        var args = new List<string>
        {
            "--permission-mode",
            config.PermissionMode ?? "auto",
            "agent",
            "stdio"
        };

        if (config.ExtraArguments is not null)
        {
            args.AddRange(config.ExtraArguments);
        }

        var process = ProcessTreeStopper.StartAgentProcess(binary, args, config.WorkingDirectory, config.ExtraEnvironment);
        var handle = new NativeAcpSessionHandle(
            Provider,
            config,
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            process,
            preferXaiExtensions: true);

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
            preferXaiExtensions: true);

        return InitializeHandleSafelyAsync(handle, config.HandshakeTimeout, cancellationToken);
    }

    public override async Task<AcpUsageSnapshot> ProbeUsageAsync(
        string? binaryPathOverride = null,
        CancellationToken cancellationToken = default)
    {
        var binary = ProcessTreeStopper.ResolveBinaryPath(binaryPathOverride ?? DefaultBinaryName);
        if (binary is null)
        {
            return new AcpUsageSnapshot(Provider, DateTimeOffset.UtcNow, SummaryText: "Grok not installed");
        }

        Process? process = null;
        AcpJsonRpcTransport? transport = null;
        try
        {
            process = ProcessTreeStopper.StartAgentProcess(binary, ["--permission-mode", "auto", "agent", "stdio"], Environment.CurrentDirectory);
            transport = new AcpJsonRpcTransport(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
            transport.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            var initParams = JsonElementBuilder.Build(static w =>
            {
                w.WriteStartObject();
                w.WriteNumber("protocolVersion", 1);
                w.WritePropertyName("clientCapabilities");
                w.WriteStartObject();
                w.WriteEndObject();
                w.WriteEndObject();
            });

            await transport.SendRequestAsync("initialize", initParams, cts.Token).ConfigureAwait(false);
            var billing = await transport.SendRequestAsync("_x.ai/billing", null, cts.Token).ConfigureAwait(false);
            return ParseGrokBillingResponse(billing);
        }
        catch
        {
            return new AcpUsageSnapshot(Provider, DateTimeOffset.UtcNow, PlanType: "xAI Grok", SummaryText: "xAI Grok Ready");
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

    internal static AcpUsageSnapshot ParseGrokBillingResponse(JsonElement billing)
    {
        var credits = billing.GetOptionalDecimal("creditsRemainingUsd") ?? billing.GetOptionalDecimal("balanceUsd");
        var plan = billing.GetStringOrDefault("planType", "xAI Grok");

        return new AcpUsageSnapshot(
            Provider: AgentProviderKind.Grok,
            CapturedAt: DateTimeOffset.UtcNow,
            PlanType: plan,
            CreditsRemainingUsd: credits,
            SummaryText: credits.HasValue ? $"Credits: ${credits.Value:0.00}" : $"Grok ({plan})");
    }
}
