using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;
using Acplite.Sdk.Transport;

namespace Acplite.Sdk.Adapters;

public sealed class AntigravityAdapter : ProviderAdapterBase
{
    private readonly string _brainRootDirectory;

    public AntigravityAdapter(string? brainRootDirectory = null)
    {
        var agyHome = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLI_HOME");
        if (string.IsNullOrWhiteSpace(agyHome))
        {
            agyHome = Path.Combine(ProcessTreeStopper.GetUserHomeDirectory(), ".gemini", "antigravity-cli");
        }
        _brainRootDirectory = brainRootDirectory ?? Path.Combine(agyHome, "brain");
    }

    public override AgentProviderKind Provider => AgentProviderKind.Antigravity;
    public override string ShortId => "agy";
    public override string DisplayName => "Google Antigravity";
    public override string DefaultBinaryName => "agy";
    public override bool IsNativeAcp => false;

    protected override IReadOnlyList<string> GetExtraBinaryCandidates()
    {
        var home = ProcessTreeStopper.GetUserHomeDirectory();
        return string.IsNullOrEmpty(home)
            ? []
            : [Path.Combine(home, ".antigravity", "bin", "agy")];
    }

    public override async Task<IReadOnlyList<AcpDiscoveredSession>> DiscoverSessionsAsync(
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_brainRootDirectory))
        {
            return [];
        }

        var candidateFiles = new List<(string ConversationId, FileInfo TranscriptFile)>();
        try
        {
            foreach (var convDir in Directory.EnumerateDirectories(_brainRootDirectory))
            {
                var convId = Path.GetFileName(convDir);
                if (string.IsNullOrWhiteSpace(convId))
                {
                    continue;
                }

                var compactPath = Path.Combine(convDir, ".system_generated", "logs", "transcript.jsonl");
                if (File.Exists(compactPath))
                {
                    candidateFiles.Add((convId, new FileInfo(compactPath)));
                }
            }
        }
        catch
        {
            return [];
        }

        var ordered = candidateFiles
            .OrderByDescending(static x => x.TranscriptFile.LastWriteTimeUtc)
            .Take(Math.Max(1, maxResults) * 2)
            .ToList();

        var results = new List<AcpDiscoveredSession>(Math.Min(maxResults, ordered.Count));
        foreach (var (convId, transcriptFile) in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = await GetOrParseSessionFileAsync(
                transcriptFile,
                (f, ct) => AntigravityTranscriptParser.TryParseSessionAsync(convId, f, ct),
                cancellationToken).ConfigureAwait(false);
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
        => AntigravityTranscriptParser.ParseTranscriptLine(sessionId, line);

    public override async Task<IAcpSessionHandle> StartSessionAsync(
        AcpSessionConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var binary = ResolveRequiredBinaryPath(config.BinaryPathOverride);

        var sessionId = !string.IsNullOrWhiteSpace(config.SessionId)
            ? config.SessionId!
            : Guid.NewGuid().ToString("D");

        var args = BuildCommandLineArguments(config);
        var process = ProcessTreeStopper.StartAgentProcess(binary, args, config.WorkingDirectory, config.ExtraEnvironment);
        var handle = new AntigravitySessionHandle(
            config,
            sessionId,
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
        var sessionId = !string.IsNullOrWhiteSpace(config.SessionId)
            ? config.SessionId!
            : Guid.NewGuid().ToString("D");

        var handle = new AntigravitySessionHandle(config, sessionId, agentOutputStream, agentInputStream, process: null);
        return InitializeHandleSafelyAsync(handle, config.HandshakeTimeout, cancellationToken);
    }

    public override async Task<AcpUsageSnapshot> ProbeUsageAsync(
        string? binaryPathOverride = null,
        CancellationToken cancellationToken = default)
    {
        var sessions = await DiscoverSessionsAsync(maxResults: 5, cancellationToken).ConfigureAwait(false);
        var activeCount = sessions.Count(static s => s.IsActive);

        return new AcpUsageSnapshot(
            Provider: Provider,
            CapturedAt: DateTimeOffset.UtcNow,
            PlanType: "Google Antigravity",
            SummaryText: sessions.Count > 0
                ? $"Antigravity ({sessions.Count} recent sessions, {activeCount} active)"
                : "Google Antigravity Ready");
    }

    private static List<string> BuildCommandLineArguments(AcpSessionConfig config)
    {
        var args = new List<string>();
        if (config.ResumeExisting && !string.IsNullOrWhiteSpace(config.SessionId))
        {
            args.Add("--conversation");
            args.Add(config.SessionId!);
        }

        args.Add("-p");
        args.Add("--input-format");
        args.Add("stream-json");
        args.Add("--output-format");
        args.Add("stream-json");

        if (!string.IsNullOrWhiteSpace(config.Model))
        {
            args.Add("--model");
            args.Add(config.Model!);
        }

        if (!string.IsNullOrWhiteSpace(config.ReasoningEffort))
        {
            args.Add("--effort");
            args.Add(config.ReasoningEffort!);
        }

        if (!string.IsNullOrWhiteSpace(config.PermissionMode))
        {
            args.Add("--mode");
            args.Add(config.PermissionMode!);
        }

        if (config.AutoApproveTools)
        {
            args.Add("--dangerously-skip-permissions");
        }

        if (config.ExtraArguments is not null)
        {
            args.AddRange(config.ExtraArguments);
        }

        return args;
    }
}
