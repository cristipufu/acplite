using System.Text;
using System.Text.Json;
using Acplite.Sdk.Formatting;
using Acplite.Sdk.Protocol;

namespace Acplite.Sdk.Adapters;

internal static class ClaudeTranscriptParser
{
    public static async Task<AcpDiscoveredSession?> TryParseSessionFileAsync(
        FileInfo file,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = Path.GetFileNameWithoutExtension(file.Name);
            string? title = null;
            string? cwd = null;
            string? gitBranch = null;
            string? model = null;
            var messageCount = 0;

            await using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);

            var linesRead = 0;
            string? line;
            while (linesRead < 60 && (line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
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

                    cwd ??= root.GetOptionalString("cwd");
                    gitBranch ??= root.GetOptionalString("gitBranch");

                    var sid = root.GetOptionalString("sessionId");
                    if (!string.IsNullOrWhiteSpace(sid))
                    {
                        sessionId = sid!;
                    }

                    var type = root.GetOptionalString("type");
                    if (type is AcpRoles.User or AcpRoles.Assistant)
                    {
                        messageCount++;
                        if (root.TryGetObject("message", out var msgObj))
                        {
                            model ??= msgObj.GetOptionalString("model");
                            if (title is null && type == AcpRoles.User)
                            {
                                title = ExtractMessageText(msgObj);
                            }
                        }
                    }
                }
                catch
                {
                    // Skip malformed line
                }
            }

            if (messageCount == 0 && title is null)
            {
                return null;
            }

            var displayTitle = !string.IsNullOrWhiteSpace(title)
                ? TextSanitizer.TruncateSingleLine(title)
                : $"Claude session {sessionId[..Math.Min(8, sessionId.Length)]}";

            return new AcpDiscoveredSession(
                Provider: AgentProviderKind.Claude,
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

        var prefix = line.AsSpan(0, Math.Min(line.Length, 256));
        if (prefix.Contains("\"type\":\"file-history-snapshot\"", StringComparison.Ordinal) ||
            prefix.Contains("\"type\":\"queue-operation\"", StringComparison.Ordinal))
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
            var type = root.GetOptionalString("type");
            if (type is not (AcpRoles.User or AcpRoles.Assistant) || !root.TryGetObject("message", out var msgObj))
            {
                yield break;
            }

            var timestamp = DateTimeOffset.UtcNow;
            var rawTs = root.GetOptionalString("timestamp");
            if (!string.IsNullOrEmpty(rawTs) && DateTimeOffset.TryParse(rawTs, out var parsedTs))
            {
                timestamp = parsedTs;
            }

            var text = ExtractMessageText(msgObj);
            var thinking = ExtractThinkingText(msgObj);
            if (!string.IsNullOrWhiteSpace(text) || !string.IsNullOrWhiteSpace(thinking))
            {
                yield return new AcpTranscriptHistoryEntryEvent(
                    Provider: AgentProviderKind.Claude,
                    SessionId: sessionId,
                    Timestamp: timestamp,
                    Role: type,
                    Text: text ?? string.Empty,
                    Thinking: thinking);
            }

            if (type == AcpRoles.Assistant && msgObj.TryGetArray("content", out var contentArr))
            {
                foreach (var item in contentArr.EnumerateArray())
                {
                    if (item.GetOptionalString("type") == "tool_use")
                    {
                        var toolName = item.GetOptionalString("name");
                        if (!string.IsNullOrWhiteSpace(toolName))
                        {
                            var toolId = item.GetStringOrDefault("id", Guid.NewGuid().ToString("N"));
                            string? argSummary = null;
                            string? rawInput = null;
                            if (item.TryGetObject("input", out var inputObj))
                            {
                                argSummary = TextSanitizer.ExtractToolArgSummary(inputObj);
                                rawInput = inputObj.GetRawText();
                            }

                            yield return new AcpToolCallEvent(
                                Provider: AgentProviderKind.Claude,
                                SessionId: sessionId,
                                Timestamp: timestamp,
                                ToolCallId: toolId,
                                ToolName: toolName!,
                                Title: !string.IsNullOrWhiteSpace(argSummary) ? argSummary! : toolName!,
                                Kind: TextSanitizer.InferToolKind(toolName),
                                Status: AcpToolStatuses.Completed,
                                RawInputJson: rawInput);
                        }
                    }
                }
            }
        }
    }

    public static string? ExtractMessageText(JsonElement msgObj)
    {
        if (!msgObj.TryGetProperty("content", out var contentProp))
        {
            return null;
        }

        if (contentProp.ValueKind == JsonValueKind.String)
        {
            return contentProp.GetString();
        }

        if (contentProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var item in contentProp.EnumerateArray())
        {
            if (item.GetOptionalString("type") == "text")
            {
                var txt = item.GetOptionalString("text");
                if (!string.IsNullOrEmpty(txt))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(txt);
                }
            }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    public static string? ExtractThinkingText(JsonElement msgObj)
    {
        if (!msgObj.TryGetArray("content", out var contentArr))
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var item in contentArr.EnumerateArray())
        {
            if (item.GetOptionalString("type") == "thinking")
            {
                var thinking = item.GetOptionalString("thinking");
                if (!string.IsNullOrEmpty(thinking))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(thinking);
                }
            }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }
}
