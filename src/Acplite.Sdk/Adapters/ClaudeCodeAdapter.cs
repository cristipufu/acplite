using System.Text.Json;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;
using Acplite.Sdk.Transport;

namespace Acplite.Sdk.Adapters;

public sealed class ClaudeCodeAdapter : ProviderAdapterBase
{
    private readonly string _projectsRootDirectory;

    public ClaudeCodeAdapter(string? projectsRootDirectory = null)
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configDir))
        {
            configDir = Path.Combine(ProcessTreeStopper.GetUserHomeDirectory(), ".claude");
        }
        _projectsRootDirectory = projectsRootDirectory ?? Path.Combine(configDir, "projects");
    }

    public override AgentProviderKind Provider => AgentProviderKind.Claude;
    public override string ShortId => "claude";
    public override string DisplayName => "Claude Code";
    public override string DefaultBinaryName => "claude";
    public override bool IsNativeAcp => false;

    public override async Task<IReadOnlyList<AcpDiscoveredSession>> DiscoverSessionsAsync(
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var files = EnumerateRecentJsonlFilesSafely(
            _projectsRootDirectory,
            maxFilesToInspect: Math.Max(1, maxResults) * 2,
            predicate: static f => !f.Name.StartsWith("agent-", StringComparison.OrdinalIgnoreCase));

        var results = new List<AcpDiscoveredSession>(Math.Min(maxResults, files.Count));
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = await GetOrParseSessionFileAsync(
                file,
                ClaudeTranscriptParser.TryParseSessionFileAsync,
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
        => ClaudeTranscriptParser.ParseTranscriptLine(sessionId, line);

    public override async Task<IAcpSessionHandle> StartSessionAsync(
        AcpSessionConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var binary = ResolveRequiredBinaryPath(config.BinaryPathOverride);
        var args = BuildCommandLineArguments(config);

        var process = ProcessTreeStopper.StartAgentProcess(binary, args, config.WorkingDirectory, config.ExtraEnvironment);
        var handle = new ClaudeSessionHandle(
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
        var handle = new ClaudeSessionHandle(config, agentOutputStream, agentInputStream, process: null);
        return InitializeHandleSafelyAsync(handle, config.HandshakeTimeout, cancellationToken);
    }

    public override Task<AcpUsageSnapshot> ProbeUsageAsync(
        string? binaryPathOverride = null,
        CancellationToken cancellationToken = default)
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configDir))
        {
            configDir = Path.Combine(ProcessTreeStopper.GetUserHomeDirectory(), ".claude");
        }

        var credsPath = Path.Combine(configDir, ".credentials.json");
        string? planType = null;

        if (File.Exists(credsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(credsPath));
                if (doc.RootElement.TryGetObject("claudeAiOauth", out var oauth))
                {
                    planType = oauth.GetOptionalString("subscriptionType");
                }
            }
            catch
            {
                // Ignore malformed credentials
            }
        }

        return Task.FromResult(new AcpUsageSnapshot(
            Provider: Provider,
            CapturedAt: DateTimeOffset.UtcNow,
            PlanType: planType ?? "Claude Code",
            SummaryText: planType is not null ? $"Claude ({planType})" : "Claude Code Ready"));
    }

    private static List<string> BuildCommandLineArguments(AcpSessionConfig config)
    {
        var args = new List<string>
        {
            "--print",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--permission-prompt-tool", "stdio"
        };

        if (config.ResumeExisting && !string.IsNullOrWhiteSpace(config.SessionId))
        {
            args.Add("--resume");
            args.Add(config.SessionId!);
        }

        if (!string.IsNullOrWhiteSpace(config.Model))
        {
            args.Add("--model");
            args.Add(config.Model!);
        }

        if (!string.IsNullOrWhiteSpace(config.PermissionMode))
        {
            args.Add("--permission-mode");
            args.Add(config.PermissionMode!);
        }
        else if (config.AutoApproveTools)
        {
            args.Add("--permission-mode");
            args.Add("bypassPermissions");
        }

        if (config.ExtraArguments is not null)
        {
            args.AddRange(config.ExtraArguments);
        }

        return args;
    }
}
