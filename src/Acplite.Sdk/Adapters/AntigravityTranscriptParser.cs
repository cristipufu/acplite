using System.Text;
using System.Text.Json;
using Acplite.Sdk.Formatting;
using Acplite.Sdk.Protocol;

namespace Acplite.Sdk.Adapters;

internal static class AntigravityTranscriptParser
{
    public static async Task<AcpDiscoveredSession?> TryParseSessionAsync(
        string conversationId,
        FileInfo transcriptFile,
        CancellationToken cancellationToken)
    {
        try
        {
            string? title = null;
            string? cwd = null;
            var messageCount = 0;

            await using var fs = new FileStream(transcriptFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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

                    if (type is "USER_INPUT" or "PLANNER_RESPONSE")
                    {
                        messageCount++;
                        if (type == "USER_INPUT")
                        {
                            var rawContent = root.GetOptionalString("content");
                            cwd ??= TextSanitizer.TryExtractAntigravityWorkspace(rawContent);
                            title ??= TextSanitizer.ExtractAntigravityUserPrompt(rawContent);
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
                : $"Antigravity {conversationId[..Math.Min(8, conversationId.Length)]}";

            return new AcpDiscoveredSession(
                Provider: AgentProviderKind.Antigravity,
                SessionId: conversationId,
                Title: displayTitle,
                WorkingDirectory: cwd,
                TranscriptPath: transcriptFile.FullName,
                UpdatedAt: new DateTimeOffset(transcriptFile.LastWriteTimeUtc, TimeSpan.Zero),
                CreatedAt: new DateTimeOffset(transcriptFile.CreationTimeUtc, TimeSpan.Zero),
                Model: "gemini",
                IsActive: (DateTime.UtcNow - transcriptFile.LastWriteTimeUtc) < TimeSpan.FromMinutes(2),
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
            var type = root.GetOptionalString("type");

            var timestamp = DateTimeOffset.UtcNow;
            var createdAt = root.GetOptionalString("created_at");
            if (!string.IsNullOrEmpty(createdAt) && DateTimeOffset.TryParse(createdAt, out var parsedTs))
            {
                timestamp = parsedTs;
            }

            if (type == "USER_INPUT")
            {
                var content = TextSanitizer.ExtractAntigravityUserPrompt(root.GetOptionalString("content"));
                if (!string.IsNullOrWhiteSpace(content))
                {
                    yield return new AcpTranscriptHistoryEntryEvent(
                        Provider: AgentProviderKind.Antigravity,
                        SessionId: sessionId,
                        Timestamp: timestamp,
                        Role: AcpRoles.User,
                        Text: content);
                }
            }
            else if (type == "PLANNER_RESPONSE")
            {
                var content = root.GetStringOrDefault("content");
                var thinking = root.GetOptionalString("thinking");

                if (!string.IsNullOrWhiteSpace(content) || !string.IsNullOrWhiteSpace(thinking))
                {
                    yield return new AcpTranscriptHistoryEntryEvent(
                        Provider: AgentProviderKind.Antigravity,
                        SessionId: sessionId,
                        Timestamp: timestamp,
                        Role: AcpRoles.Assistant,
                        Text: content,
                        Thinking: thinking,
                        ToolName: null);
                }

                if (root.TryGetArray("tool_calls", out var tcArr))
                {
                    foreach (var tc in tcArr.EnumerateArray())
                    {
                        var toolName = tc.GetOptionalString("name") ?? tc.GetOptionalString("tool_name");
                        if (!string.IsNullOrWhiteSpace(toolName))
                        {
                            var toolId = tc.GetStringOrDefault("id", Guid.NewGuid().ToString("N"));
                            string? argSummary = null;
                            string? rawInput = null;
                            if (tc.TryGetObject("args", out var argsObj))
                            {
                                argSummary = TextSanitizer.ExtractToolArgSummary(argsObj);
                                rawInput = argsObj.GetRawText();
                            }

                            yield return new AcpToolCallEvent(
                                Provider: AgentProviderKind.Antigravity,
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
}
