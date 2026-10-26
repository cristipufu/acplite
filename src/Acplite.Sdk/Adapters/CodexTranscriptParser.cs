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
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{' || line.Length > 256 * 1024)
                {
                    continue;
                }

                var prefix = line.AsSpan(0, Math.Min(line.Length, 320));
                if (prefix.Contains("\"type\":\"world_state\"", StringComparison.Ordinal) ||
                    prefix.Contains("\"type\":\"token_usage_record\"", StringComparison.Ordinal) ||
                    prefix.Contains("\"type\":\"custom_tool_call_output\"", StringComparison.Ordinal) ||
                    prefix.Contains("\"type\":\"function_call_output\"", StringComparison.Ordinal))
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
                            var id = payload.GetOptionalString("id") ?? payload.GetOptionalString("session_id");
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
                            cwd ??= payload.GetOptionalString("cwd");
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
                                    var msg = payload.GetOptionalString("message");
                                    if (!TextSanitizer.IsCodexInjectedUserContext(msg))
                                    {
                                        title = TextSanitizer.ExtractCleanUserTitle(msg);
                                    }
                                }
                            }
                            else if (evType == "thread_settings_applied" &&
                                     payload.TryGetObject("thread_settings", out var ts))
                            {
                                model ??= ts.GetOptionalString("model");
                            }
                            break;
                        }

                        case "response_item":
                        {
                            var itemType = payload.GetOptionalString("type");
                            if (itemType == "message")
                            {
                                var role = payload.GetOptionalString("role");
                                if (role is "user" or "assistant")
                                {
                                    var msg = ExtractCodexMessageContent(payload);
                                    if (role == "user")
                                    {
                                        if (!TextSanitizer.IsCodexInjectedUserContext(msg))
                                        {
                                            messageCount++;
                                            title ??= TextSanitizer.ExtractCleanUserTitle(msg);
                                        }
                                    }
                                    else if (!string.IsNullOrWhiteSpace(msg))
                                    {
                                        messageCount++;
                                    }
                                }
                            }
                            break;
                        }
                    }

                    if (title is not null && cwd is not null && model is not null)
                    {
                        break;
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

        var prefix = line.AsSpan(0, Math.Min(line.Length, 320));
        if (prefix.Contains("\"type\":\"token_usage_record\"", StringComparison.Ordinal) ||
            prefix.Contains("\"type\":\"world_state\"", StringComparison.Ordinal) ||
            prefix.Contains("\"type\":\"compacted\"", StringComparison.Ordinal) ||
            prefix.Contains("\"type\":\"inter_agent_communication_metadata\"", StringComparison.Ordinal) ||
            prefix.Contains("\"type\":\"session_meta\"", StringComparison.Ordinal) ||
            prefix.Contains("\"type\":\"turn_context\"", StringComparison.Ordinal) ||
            prefix.Contains("\"type\":\"token_count\"", StringComparison.Ordinal) ||
            prefix.Contains("\"type\":\"custom_tool_call_output\"", StringComparison.Ordinal) ||
            prefix.Contains("\"type\":\"function_call_output\"", StringComparison.Ordinal) ||
            prefix.Contains("\"type\":\"item_completed\"", StringComparison.Ordinal))
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
            var rootType = root.GetOptionalString("type");
            if (rootType is not ("event_msg" or "response_item") || !root.TryGetObject("payload", out var payload))
            {
                yield break;
            }

            var timestamp = DateTimeOffset.UtcNow;
            var rawTs = root.GetOptionalString("timestamp");
            if (!string.IsNullOrEmpty(rawTs) && DateTimeOffset.TryParse(rawTs, out var parsedTs))
            {
                timestamp = parsedTs;
            }

            if (rootType == "event_msg")
            {
                var evType = payload.GetOptionalString("type");
                switch (evType)
                {
                    case "user_message":
                    {
                        var message = payload.GetOptionalString("message");
                        if (!string.IsNullOrEmpty(message) && !TextSanitizer.IsCodexInjectedUserContext(message))
                        {
                            yield return new AcpTranscriptHistoryEntryEvent(AgentProviderKind.Codex, sessionId, timestamp, "user", message.Trim());
                        }
                        break;
                    }

                    case "agent_message":
                    {
                        var message = payload.GetOptionalString("message");
                        if (!string.IsNullOrEmpty(message))
                        {
                            yield return new AcpTranscriptHistoryEntryEvent(AgentProviderKind.Codex, sessionId, timestamp, "assistant", message.Trim());
                        }
                        break;
                    }

                    case "agent_reasoning":
                    {
                        var reasoning = payload.GetOptionalString("text");
                        if (!string.IsNullOrEmpty(reasoning))
                        {
                            yield return new AcpTranscriptHistoryEntryEvent(AgentProviderKind.Codex, sessionId, timestamp, "assistant", string.Empty, Thinking: reasoning.Trim());
                        }
                        break;
                    }
                }

                yield break;
            }

            var itemType = payload.GetOptionalString("type");
            switch (itemType)
            {
                case "message":
                {
                    var role = payload.GetOptionalString("role");
                    if (role == "user")
                    {
                        var text = ExtractCodexMessageContent(payload);
                        if (!string.IsNullOrWhiteSpace(text) && !TextSanitizer.IsCodexInjectedUserContext(text))
                        {
                            yield return new AcpTranscriptHistoryEntryEvent(
                                Provider: AgentProviderKind.Codex,
                                SessionId: sessionId,
                                Timestamp: timestamp,
                                Role: "user",
                                Text: text);
                        }
                    }
                    else if (role == "assistant")
                    {
                        var text = ExtractCodexMessageContent(payload);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            yield return new AcpTranscriptHistoryEntryEvent(
                                Provider: AgentProviderKind.Codex,
                                SessionId: sessionId,
                                Timestamp: timestamp,
                                Role: "assistant",
                                Text: text);
                        }
                    }
                    break;
                }

                case "reasoning":
                {
                    var thinkingText = ExtractCodexReasoningText(payload);
                    if (!string.IsNullOrWhiteSpace(thinkingText))
                    {
                        yield return new AcpTranscriptHistoryEntryEvent(
                            Provider: AgentProviderKind.Codex,
                            SessionId: sessionId,
                            Timestamp: timestamp,
                            Role: "assistant",
                            Text: string.Empty,
                            Thinking: thinkingText);
                    }
                    break;
                }

                case "custom_tool_call":
                case "function_call":
                case "local_shell_call":
                {
                    var (toolName, summary, rawInput) = ExtractCodexToolCall(payload, itemType!);
                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        var callId = payload.GetStringOrDefault("call_id",
                            payload.GetStringOrDefault("id", Guid.NewGuid().ToString("N")));
                        yield return new AcpToolCallEvent(
                            Provider: AgentProviderKind.Codex,
                            SessionId: sessionId,
                            Timestamp: timestamp,
                            ToolCallId: callId,
                            ToolName: toolName,
                            Title: !string.IsNullOrWhiteSpace(summary) ? summary : toolName,
                            Kind: TextSanitizer.InferToolKind(toolName),
                            Status: "completed",
                            RawInputJson: rawInput);
                    }
                    break;
                }

                case "agent_message":
                {
                    var author = payload.GetOptionalString("author");
                    var recipient = payload.GetOptionalString("recipient");
                    var text = ExtractCodexMessageContent(payload);
                    var (summary, cleanBody) = FormatSubagentMessageSummary(author, recipient, text);
                    var callId = payload.GetStringOrDefault("id", Guid.NewGuid().ToString("N"));

                    yield return new AcpToolCallEvent(
                        Provider: AgentProviderKind.Codex,
                        SessionId: sessionId,
                        Timestamp: timestamp,
                        ToolCallId: callId,
                        ToolName: "subagent",
                        Title: summary,
                        Kind: "other",
                        Status: "completed",
                        RawInputJson: cleanBody);
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

    private static string ExtractCodexMessageContent(JsonElement payload)
    {
        if (payload.TryGetArray("content", out var contentArr))
        {
            var sb = new StringBuilder();
            foreach (var item in contentArr.EnumerateArray())
            {
                var itemType = item.GetOptionalString("type");
                if (itemType is "input_text" or "output_text" or "text")
                {
                    var text = item.GetOptionalString("text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append('\n');
                        }
                        sb.Append(text);
                    }
                }
            }
            return sb.ToString().Trim();
        }

        return payload.GetOptionalString("message")?.Trim() ?? string.Empty;
    }

    private static string ExtractCodexReasoningText(JsonElement payload)
    {
        if (payload.TryGetArray("summary", out var summaryArr))
        {
            var sb = new StringBuilder();
            foreach (var item in summaryArr.EnumerateArray())
            {
                var text = item.GetOptionalString("text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }
                    sb.Append(text!.Trim());
                }
            }
            if (sb.Length > 0)
            {
                return sb.ToString();
            }
        }

        if (payload.TryGetArray("content", out var contentArr))
        {
            var sb = new StringBuilder();
            foreach (var item in contentArr.EnumerateArray())
            {
                var text = item.GetOptionalString("text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }
                    sb.Append(text!.Trim());
                }
            }
            if (sb.Length > 0)
            {
                return sb.ToString();
            }
        }

        return payload.GetOptionalString("text")?.Trim() ?? string.Empty;
    }

    private static (string ToolName, string Summary, string? RawInput) ExtractCodexToolCall(JsonElement payload, string itemType)
    {
        if (itemType == "local_shell_call")
        {
            if (payload.TryGetObject("action", out var action) &&
                action.TryGetArray("command", out var cmdArr))
            {
                var parts = new List<string>();
                foreach (var c in cmdArr.EnumerateArray())
                {
                    var s = c.GetString();
                    if (!string.IsNullOrEmpty(s))
                    {
                        parts.Add(s);
                    }
                }
                var fullCmd = parts.Count >= 3 && parts[1] == "-lc"
                    ? parts[2]
                    : string.Join(' ', parts);
                var cleaned = TextSanitizer.TruncateSingleLine(fullCmd, 120);
                return ("shell", string.IsNullOrWhiteSpace(cleaned) ? "shell" : cleaned, fullCmd);
            }
            return ("shell", "shell", null);
        }

        var name = payload.GetOptionalString("name") ?? itemType;
        var rawInput = payload.GetOptionalString("input") ?? payload.GetOptionalString("arguments");

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return (name, name, null);
        }

        var trimmed = rawInput.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var argDoc = JsonDocument.Parse(trimmed);
                var argSummary = TextSanitizer.ExtractToolArgSummary(argDoc.RootElement);
                if (!string.IsNullOrWhiteSpace(argSummary))
                {
                    return (name, argSummary!, trimmed);
                }
            }
            catch
            {
                // Fall through to JS/text extraction
            }
        }

        var jsCmd = TextSanitizer.TryExtractJsStringProperty(trimmed, "cmd")
                    ?? TextSanitizer.TryExtractJsStringProperty(trimmed, "command")
                    ?? TextSanitizer.TryExtractJsStringProperty(trimmed, "path")
                    ?? TextSanitizer.TryExtractJsStringProperty(trimmed, "chars");
        if (!string.IsNullOrWhiteSpace(jsCmd))
        {
            return (name, TextSanitizer.TruncateSingleLine(jsCmd!, 120), jsCmd);
        }

        var sessionIdIdx = trimmed.IndexOf("session_id:", StringComparison.Ordinal);
        if (sessionIdIdx >= 0)
        {
            var after = trimmed[(sessionIdIdx + "session_id:".Length)..].TrimStart();
            var numLen = 0;
            while (numLen < after.Length && char.IsDigit(after[numLen]))
            {
                numLen++;
            }
            if (numLen > 0)
            {
                return (name, $"cell {after[..numLen]}", trimmed);
            }
        }

        return (name, TextSanitizer.TruncateSingleLine(trimmed, 120), trimmed);
    }

    private static (string Summary, string? CleanBody) FormatSubagentMessageSummary(
        string? author,
        string? recipient,
        string? rawText)
    {
        var shortAuthor = !string.IsNullOrWhiteSpace(author)
            ? TextSanitizer.ShortenPathForDisplay(author!)
            : "subagent";

        if (string.IsNullOrWhiteSpace(rawText))
        {
            var target = !string.IsNullOrWhiteSpace(recipient)
                ? $" -> {TextSanitizer.ShortenPathForDisplay(recipient!)}"
                : string.Empty;
            return ($"{shortAuthor}{target}", null);
        }

        var payloadText = rawText!;
        const string payloadMarker = "Payload:";
        var idx = payloadText.IndexOf(payloadMarker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            payloadText = payloadText[(idx + payloadMarker.Length)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(payloadText))
        {
            return ($"{shortAuthor} update", null);
        }

        var firstLine = payloadText;
        var nl = firstLine.IndexOf('\n');
        if (nl >= 0)
        {
            firstLine = firstLine[..nl].Trim();
        }

        var summary = $"{shortAuthor}: {TextSanitizer.TruncateSingleLine(firstLine, 100)}";
        return (summary, payloadText);
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
