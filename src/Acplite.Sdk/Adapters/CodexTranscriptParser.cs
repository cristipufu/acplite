using System.Text;
using System.Text.Json;
using Acplite.Sdk.Formatting;
using Acplite.Sdk.Protocol;

namespace Acplite.Sdk.Adapters;

internal static class CodexTranscriptParser
{
    public static async Task<AcpDiscoveredSession?> TryParseSessionFileAsync(
        FileInfo file,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = ExtractUuidFromFileName(file.Name);
            string? title = null;
            string? cwd = null;
            string? gitBranch = null;
            string? model = null;
            var messageCount = 0;

            await using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);

            var linesRead = 0;
            string? line;
            while (linesRead < 80 && (line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                linesRead++;
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.GetOptionalString("type");
                    if (!root.TryGetObject("payload", out var payload))
                    {
                        continue;
                    }

                    switch (type)
                    {
                        case "session_meta":
                        {
                            var id = payload.GetOptionalString("id");
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                sessionId = id!;
                            }
                            cwd ??= payload.GetOptionalString("cwd");
                            if (gitBranch is null && payload.TryGetObject("git", out var gitObj))
                            {
                                gitBranch = gitObj.GetOptionalString("branch");
                            }
                            break;
                        }

                        case "turn_context":
                            model ??= payload.GetOptionalString("model");
                            break;

                        case "event_msg":
                        {
                            var evType = payload.GetOptionalString("type");
                            if (evType is "user_message" or "agent_message")
                            {
                                messageCount++;
                                if (title is null && evType == "user_message")
                                {
                                    title = payload.GetOptionalString("message");
                                }
                            }
                            break;
                        }
                    }
                }
                catch
                {
                    // Skip malformed line
                }
            }

            var displayTitle = !string.IsNullOrWhiteSpace(title)
                ? TextSanitizer.TruncateSingleLine(title)
                : $"Codex session {sessionId[..Math.Min(8, sessionId.Length)]}";

            return new AcpDiscoveredSession(
                Provider: AgentProviderKind.Codex,
                SessionId: sessionId,
                Title: displayTitle,
                WorkingDirectory: cwd,
                TranscriptPath: file.FullName,
                UpdatedAt: new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                CreatedAt: new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
                Model: model,
                GitBranch: gitBranch,
                IsActive: (DateTime.UtcNow - file.LastWriteTimeUtc) < TimeSpan.FromMinutes(2),
                MessageCount: messageCount);
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<AcpEvent> ParseTranscriptLine(string sessionId, string line)
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
            if (root.GetOptionalString("type") != "event_msg" || !root.TryGetObject("payload", out var payload))
            {
                yield break;
            }

            var timestamp = DateTimeOffset.UtcNow;
            var rawTs = root.GetOptionalString("timestamp");
            if (!string.IsNullOrEmpty(rawTs) && DateTimeOffset.TryParse(rawTs, out var parsedTs))
            {
                timestamp = parsedTs;
            }

            var evType = payload.GetOptionalString("type");
            switch (evType)
            {
                case "user_message":
                {
                    var message = payload.GetOptionalString("message");
                    if (!string.IsNullOrEmpty(message))
                    {
                        yield return new AcpTranscriptHistoryEntryEvent(AgentProviderKind.Codex, sessionId, timestamp, "user", message);
                    }
                    break;
                }

                case "agent_message":
                {
                    var message = payload.GetOptionalString("message");
                    if (!string.IsNullOrEmpty(message))
                    {
                        yield return new AcpTranscriptHistoryEntryEvent(AgentProviderKind.Codex, sessionId, timestamp, "assistant", message);
                    }
                    break;
                }

                case "agent_reasoning":
                {
                    var reasoning = payload.GetOptionalString("text");
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        yield return new AcpTranscriptHistoryEntryEvent(AgentProviderKind.Codex, sessionId, timestamp, "assistant", string.Empty, Thinking: reasoning);
                    }
                    break;
                }
            }
        }
    }

    public static AcpUsageSnapshot ParseRateLimitsResponse(JsonElement root)
    {
        var source = root.TryGetObject("rateLimits", out var rl) ? rl : root;
        var planType = source.GetStringOrDefault("planType", "Codex");
        double? fiveHourPct = null;
        DateTimeOffset? fiveHourReset = null;
        double? sevenDayPct = null;
        DateTimeOffset? sevenDayReset = null;

        if (source.TryGetObject("primary", out var primary))
        {
            fiveHourPct = primary.GetOptionalDouble("usedPercent");
            var epochSec = primary.GetOptionalInt64("resetsAt");
            if (epochSec.HasValue)
            {
                fiveHourReset = DateTimeOffset.FromUnixTimeSeconds(epochSec.Value);
            }
        }

        if (source.TryGetObject("secondary", out var secondary))
        {
            sevenDayPct = secondary.GetOptionalDouble("usedPercent");
            var epochSec = secondary.GetOptionalInt64("resetsAt");
            if (epochSec.HasValue)
            {
                sevenDayReset = DateTimeOffset.FromUnixTimeSeconds(epochSec.Value);
            }
        }

        return new AcpUsageSnapshot(
            Provider: AgentProviderKind.Codex,
            CapturedAt: DateTimeOffset.UtcNow,
            PlanType: planType,
            FiveHourPercentUsed: fiveHourPct,
            FiveHourResetsAt: fiveHourReset,
            SevenDayPercentUsed: sevenDayPct,
            SevenDayResetsAt: sevenDayReset,
            SummaryText: fiveHourPct.HasValue ? $"5h: {fiveHourPct.Value:0.#}% used" : $"Codex ({planType})");
    }

    private static string ExtractUuidFromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length >= 36)
        {
            var candidate = stem[^36..];
            if (Guid.TryParse(candidate, out _))
            {
                return candidate;
            }
        }
        return stem;
    }
}
