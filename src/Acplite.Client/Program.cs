using System.CommandLine;
using System.Text.Json;
using Acplite.Sdk;
using Acplite.Sdk.Bridge;
using Acplite.Sdk.Protocol;
using Spectre.Console;

namespace Acplite.Client;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Native C# / NativeAOT Agent Client Protocol (ACP) CLI and provider bridges for Claude Code, OpenAI Codex, Grok, and Google Antigravity.");
        var jsonOption = new Option<bool>("--json", "Emit machine-readable JSON output.");

        rootCommand.AddCommand(BuildProvidersCommand(jsonOption));
        rootCommand.AddCommand(BuildSessionsCommand(jsonOption));
        rootCommand.AddCommand(BuildTailCommand());
        rootCommand.AddCommand(BuildServeCommand());
        rootCommand.AddCommand(BuildRunCommand());
        rootCommand.AddCommand(BuildUsageCommand());

        return await rootCommand.InvokeAsync(args).ConfigureAwait(false);
    }

    private static Command BuildProvidersCommand(Option<bool> jsonOption)
    {
        var cmd = new Command("providers", "Discover installed coding agent CLIs (claude, codex, grok, agy) and their capabilities.");
        cmd.AddAlias("discover");
        cmd.AddOption(jsonOption);
        cmd.SetHandler((bool asJson) => ExecuteSafelyAsync(async ct =>
        {
            await using var runtime = new AcpliteRuntime();
            var descriptors = await runtime.DiscoverProvidersAsync(cancellationToken: ct).ConfigureAwait(false);
            if (asJson)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(descriptors, AcpJsonContext.Default.IReadOnlyListAcpProviderDescriptor));
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Provider")
                .AddColumn("ID")
                .AddColumn("Installed")
                .AddColumn("Version")
                .AddColumn("Binary Path")
                .AddColumn("Capabilities");

            foreach (var d in descriptors)
            {
                var caps = new List<string>
                {
                    d.IsNativeAcp ? "native-acp" : "bridged-acp"
                };
                if (d.SupportsLiveSteering) caps.Add("live-steer");
                if (d.SupportsSessionDiscovery) caps.Add("session-tail");
                if (d.SupportsUsageProbe) caps.Add("usage-probe");

                table.AddRow(
                    new Text(TerminalText.Clean(d.DisplayName)),
                    new Text(TerminalText.Clean(d.ShortId), new Style(Color.Cyan1)),
                    new Text(d.IsInstalled ? "Yes" : "No", new Style(d.IsInstalled ? Color.Green : Color.Grey)),
                    new Text(TerminalText.Clean(d.Version ?? "-")),
                    new Text(TerminalText.Clean(d.ResolvedBinaryPath ?? "-")),
                    new Text(string.Join(", ", caps)));
            }

            AnsiConsole.Write(table);
        }), jsonOption);

        return cmd;
    }

    private static Command BuildSessionsCommand(Option<bool> jsonOption)
    {
        var cmd = new Command("sessions", "List discovered sessions on disk across all or a specific coding agent.");
        var providerArg = new Argument<string?>("provider", () => null, "Optional provider filter: claude, codex, grok, or agy.");
        var limitOpt = new Option<int>("--limit", () => 25, "Maximum sessions per provider.");
        cmd.AddArgument(providerArg);
        cmd.AddOption(limitOpt);
        cmd.AddOption(jsonOption);

        cmd.SetHandler((string? providerStr, int limit, bool asJson) => ExecuteSafelyAsync(async ct =>
        {
            var filter = ParseOptionalProviderOrThrow(providerStr);
            await using var runtime = new AcpliteRuntime();
            var sessions = await runtime.DiscoverSessionsAsync(filter, Math.Clamp(limit, 1, 500), ct).ConfigureAwait(false);
            if (asJson)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(sessions, AcpJsonContext.Default.IReadOnlyListAcpDiscoveredSession));
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Provider")
                .AddColumn("Session ID")
                .AddColumn("Updated")
                .AddColumn("Title")
                .AddColumn("Workspace");

            foreach (var s in sessions)
            {
                table.AddRow(
                    new Text(s.Provider.ToShortId(), new Style(Color.Cyan1)),
                    new Text(TerminalText.Clean(s.SessionId)),
                    new Text(s.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                    new Text(TerminalText.Clean(s.Title, 80)),
                    new Text(TerminalText.Clean(s.WorkingDirectory ?? "-", 60)));
            }

            AnsiConsole.Write(table);
        }), providerArg, limitOpt, jsonOption);

        return cmd;
    }

    private static Command BuildTailCommand()
    {
        var cmd = new Command("tail", "Live-tail a session transcript (.jsonl) in real time.");
        var sessionIdArg = new Argument<string>("sessionId", "Full or prefix session ID to tail.");
        var noFollowOpt = new Option<bool>("--no-follow", "Print existing transcript history and exit without watching for new turns.");
        cmd.AddArgument(sessionIdArg);
        cmd.AddOption(noFollowOpt);

        cmd.SetHandler((string sessionId, bool noFollow) => ExecuteSafelyAsync(async ct =>
        {
            await using var runtime = new AcpliteRuntime();
            var session = await runtime.FindSessionByIdAsync(sessionId, cancellationToken: ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Session '{TerminalText.Clean(sessionId)}' was not found.");

            AnsiConsole.Write(new Text($"Tailing [{session.Provider.ToShortId()}] {TerminalText.Clean(session.SessionId)} - {TerminalText.Clean(session.Title)}\n\n", new Style(Color.Cyan1)));

            await foreach (var ev in runtime.TailSessionAsync(session, includeHistory: true, followLive: !noFollow, ct).ConfigureAwait(false))
            {
                if (ev is AcpTranscriptHistoryEntryEvent entry)
                {
                    var roleColor = entry.Role == "user" ? Color.Green : Color.DodgerBlue1;
                    AnsiConsole.Write(new Text($"[{entry.Timestamp.ToLocalTime():HH:mm:ss}] {entry.Role.ToUpperInvariant()}: ", new Style(roleColor, decoration: Decoration.Bold)));
                    if (!string.IsNullOrWhiteSpace(entry.Thinking))
                    {
                        AnsiConsole.Write(new Text($"(thinking) {TerminalText.Clean(entry.Thinking)}\n", new Style(Color.Grey)));
                    }
                    AnsiConsole.Write(new Text($"{TerminalText.Clean(entry.Text)}\n\n"));
                }
                else if (ev is AcpToolCallEvent tc)
                {
                    AnsiConsole.Write(new Text($"  [tool] {TerminalText.Clean(tc.ToolName)} ({TerminalText.Clean(tc.Status)})\n", new Style(Color.Yellow)));
                }
            }
        }), sessionIdArg, noFollowOpt);

        return cmd;
    }

    private static Command BuildServeCommand()
    {
        var cmd = new Command("serve", "Run a standards-compliant stdio ACP JSON-RPC 2.0 server bridge for the specified provider.");
        var serveProviderArg = new Argument<string>("provider", "Provider to bridge over stdio: claude, codex, grok, or agy.");
        var binaryOpt = new Option<string?>("--binary", "Optional override path to the underlying provider CLI executable.");
        cmd.AddArgument(serveProviderArg);
        cmd.AddOption(binaryOpt);

        cmd.SetHandler((string providerStr, string? binaryOverride) => ExecuteSafelyAsync(async ct =>
        {
            var kind = ParseRequiredProviderOrThrow(providerStr);
            await using var runtime = new AcpliteRuntime();
            var adapter = runtime.GetAdapter(kind);
            await using var bridge = new AcpStdioServerBridge(
                adapter,
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                binaryOverride);

            await bridge.RunAsync(ct).ConfigureAwait(false);
        }), serveProviderArg, binaryOpt);

        return cmd;
    }

    private static Command BuildRunCommand()
    {
        var cmd = new Command("run", "Run a prompt turn against a coding agent via its ACP adapter.");
        var runProviderArg = new Argument<string>("provider", "Provider to run: claude, codex, grok, or agy.");
        var runPromptArg = new Argument<string>("prompt", "Prompt text to send.");
        var sessionOpt = new Option<string?>("--session", "Optional session/conversation ID to resume.");
        var cwdOpt = new Option<string>("--cwd", () => Environment.CurrentDirectory, "Working directory for the session.");
        var modelOpt = new Option<string?>("--model", "Optional model identifier.");
        var autoApproveOpt = new Option<bool>("--auto-approve", () => true, "Automatically approve tool requests.");
        cmd.AddArgument(runProviderArg);
        cmd.AddArgument(runPromptArg);
        cmd.AddOption(sessionOpt);
        cmd.AddOption(cwdOpt);
        cmd.AddOption(modelOpt);
        cmd.AddOption(autoApproveOpt);

        cmd.SetHandler((string providerStr, string prompt, string? sessionId, string cwd, string? model, bool autoApprove) => ExecuteSafelyAsync(async ct =>
        {
            var kind = ParseRequiredProviderOrThrow(providerStr);
            await using var runtime = new AcpliteRuntime();
            await using var handle = await runtime.StartSessionAsync(new AcpSessionConfig
            {
                Provider = kind,
                WorkingDirectory = cwd,
                SessionId = sessionId,
                ResumeExisting = !string.IsNullOrWhiteSpace(sessionId),
                Model = model,
                AutoApproveTools = autoApprove
            }, ct).ConfigureAwait(false);

            await handle.SendPromptAsync(prompt, ct).ConfigureAwait(false);

            await foreach (var ev in handle.ReadEventsAsync(ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case AcpThinkingDeltaEvent thought:
                        AnsiConsole.Write(new Text(TerminalText.Clean(thought.Delta), new Style(Color.Grey)));
                        break;
                    case AcpAssistantTextDeltaEvent text:
                        AnsiConsole.Write(new Text(TerminalText.Clean(text.Delta)));
                        break;
                    case AcpToolCallEvent tool:
                        AnsiConsole.Write(new Text($"\n[tool:{TerminalText.Clean(tool.ToolName)} {TerminalText.Clean(tool.Status)}]\n", new Style(Color.Yellow)));
                        break;
                    case AcpInterruptionEvent interruption:
                        AnsiConsole.Write(new Text($"\n[interruption:{interruption.Classification.Kind}] {TerminalText.Clean(interruption.Classification.Message)}\n", new Style(Color.Red)));
                        break;
                    case AcpTurnCompletedEvent:
                        Console.Out.WriteLine();
                        return;
                }
            }
        }), runProviderArg, runPromptArg, sessionOpt, cwdOpt, modelOpt, autoApproveOpt);

        return cmd;
    }

    private static Command BuildUsageCommand()
    {
        var cmd = new Command("usage", "Probe account rate-limit & quota usage across coding agents.");
        var usageProviderArg = new Argument<string?>("provider", () => null, "Optional provider filter: claude, codex, grok, or agy.");
        cmd.AddArgument(usageProviderArg);

        cmd.SetHandler((string? providerStr) => ExecuteSafelyAsync(async ct =>
        {
            var filter = ParseOptionalProviderOrThrow(providerStr);
            await using var runtime = new AcpliteRuntime();
            var snapshots = await runtime.ProbeUsageAsync(filter, ct).ConfigureAwait(false);
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Provider")
                .AddColumn("Plan")
                .AddColumn("5h Window")
                .AddColumn("7d Window")
                .AddColumn("Summary");

            foreach (var s in snapshots)
            {
                table.AddRow(
                    new Text(s.Provider.ToShortId(), new Style(Color.Cyan1)),
                    new Text(TerminalText.Clean(s.PlanType ?? "-")),
                    new Text(s.FiveHourPercentUsed.HasValue ? $"{s.FiveHourPercentUsed.Value:0.#}%" : "-"),
                    new Text(s.SevenDayPercentUsed.HasValue ? $"{s.SevenDayPercentUsed.Value:0.#}%" : "-"),
                    new Text(TerminalText.Clean(s.SummaryText ?? "-")));
            }

            AnsiConsole.Write(table);
        }), usageProviderArg);

        return cmd;
    }

    private static AgentProviderKind? ParseOptionalProviderOrThrow(string? providerStr)
    {
        if (string.IsNullOrWhiteSpace(providerStr))
        {
            return null;
        }
        return ParseRequiredProviderOrThrow(providerStr);
    }

    private static AgentProviderKind ParseRequiredProviderOrThrow(string providerStr)
    {
        if (AgentProviderParser.TryParse(providerStr, out var kind))
        {
            return kind;
        }
        throw new ArgumentException($"Unknown provider '{TerminalText.Clean(providerStr)}'. Expected: claude, codex, grok, or agy.");
    }

    private static async Task ExecuteSafelyAsync(Func<CancellationToken, Task> action)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.CancelKeyPress += handler;
        try
        {
            await action(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Clean user cancellation via Ctrl+C
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(TerminalText.Clean(ex.Message));
            Environment.ExitCode = 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
