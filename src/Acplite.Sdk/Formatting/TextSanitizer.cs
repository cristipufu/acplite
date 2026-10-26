using System.Text;
using System.Text.Json;
using Acplite.Sdk.Protocol;

namespace Acplite.Sdk.Formatting;

/// <summary>
/// Pure helper methods for normalizing single-line session titles, extracting user requests,
/// summarizing tool call arguments, and inferring tool categories across adapters.
/// </summary>
internal static class TextSanitizer
{
    public static string TruncateSingleLine(string? input, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var single = input.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (single.Length <= maxLength)
        {
            return single;
        }

        return single[..Math.Max(1, maxLength - 1)] + "…";
    }

    public static string InferToolKind(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return "other";
        }

        var lower = toolName.ToLowerInvariant();
        if (lower.Contains("bash") || lower.Contains("command") || lower.Contains("exec") || lower.Contains("shell"))
        {
            return "execute";
        }
        if (lower.Contains("edit") || lower.Contains("write") || lower.Contains("patch") || lower.Contains("filechange"))
        {
            return "edit";
        }
        if (lower.Contains("read") || lower.Contains("glob") || lower.Contains("grep") || lower.Contains("search") || lower.Contains("find"))
        {
            return "read";
        }
        return "other";
    }

    public static string ExtractAntigravityUserPrompt(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return string.Empty;
        }

        const string startTag = "<USER_REQUEST>";
        const string endTag = "</USER_REQUEST>";
        var startIndex = rawContent.IndexOf(startTag, StringComparison.Ordinal);
        if (startIndex >= 0)
        {
            startIndex += startTag.Length;
            var endIndex = rawContent.IndexOf(endTag, startIndex, StringComparison.Ordinal);
            if (endIndex > startIndex)
            {
                return rawContent[startIndex..endIndex].Trim();
            }
        }

        return rawContent.Trim();
    }

    public static string? TryExtractAntigravityWorkspace(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return null;
        }

        const string marker = "Command Working Directory:";
        var index = rawContent.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var remainder = rawContent[(index + marker.Length)..];
        var newlineIndex = remainder.IndexOf('\n');
        var line = (newlineIndex >= 0 ? remainder[..newlineIndex] : remainder).Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    public static bool IsCodexInjectedUserContext(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var trimmed = text.AsSpan().TrimStart();
        return trimmed.StartsWith("# AGENTS.md instructions", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<INSTRUCTIONS>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<environment_context>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<permissions instructions>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<skills_instructions>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<collaboration_mode>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<multi_agent_", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<subagent_notification>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<codex_internal_context>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<system-reminder>", StringComparison.OrdinalIgnoreCase);
    }

    public static string ExtractCleanUserTitle(string? text, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        const string requestMarker = "## My request for Codex:";
        var markerIdx = text.IndexOf(requestMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx >= 0)
        {
            var after = text[(markerIdx + requestMarker.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(after))
            {
                return TruncateSingleLine(after, maxLength);
            }
        }

        return TruncateSingleLine(text, maxLength);
    }

    public static string? ExtractToolArgSummary(JsonElement argsObj)
    {
        if (argsObj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        ReadOnlySpan<string> pathKeys =
        [
            "Path", "path", "file_path", "filePath",
            "TargetFile", "target_file", "SearchPath",
            "Directory", "directory", "cwd"
        ];

        foreach (var key in pathKeys)
        {
            var val = argsObj.GetOptionalString(key);
            if (!string.IsNullOrWhiteSpace(val))
            {
                var shortPath = ShortenPathForDisplay(val!);
                var startLine = argsObj.GetOptionalInt64("StartLine") ?? argsObj.GetOptionalInt64("start_line");
                var endLine = argsObj.GetOptionalInt64("EndLine") ?? argsObj.GetOptionalInt64("end_line");
                if (startLine.HasValue && endLine.HasValue)
                {
                    return $"{shortPath}:{startLine.Value}-{endLine.Value}";
                }
                if (startLine.HasValue)
                {
                    return $"{shortPath}:{startLine.Value}";
                }
                return shortPath;
            }
        }

        var cmd = argsObj.GetOptionalString("CommandLine")
                  ?? argsObj.GetOptionalString("command")
                  ?? argsObj.GetOptionalString("cmd");
        var toolSummary = argsObj.GetOptionalString("toolSummary");

        if (!string.IsNullOrWhiteSpace(toolSummary) && !string.IsNullOrWhiteSpace(cmd))
        {
            return $"{toolSummary!.Trim()} ({TruncateSingleLine(cmd!, 80)})";
        }
        if (!string.IsNullOrWhiteSpace(cmd))
        {
            return TruncateSingleLine(cmd!, 120);
        }
        if (!string.IsNullOrWhiteSpace(toolSummary))
        {
            return TruncateSingleLine(toolSummary!, 120);
        }

        ReadOnlySpan<string> targetKeys =
        [
            "Query", "query", "Pattern", "pattern",
            "Url", "url", "target", "agent", "agent_path", "recipient",
            "TaskId", "chars", "Description", "description",
            "Prompt", "prompt"
        ];

        foreach (var key in targetKeys)
        {
            var val = argsObj.GetOptionalString(key);
            if (!string.IsNullOrWhiteSpace(val))
            {
                return TruncateSingleLine(val!, 100);
            }
        }

        foreach (var prop in argsObj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return TruncateSingleLine(s!, 80);
                }
            }
        }

        return null;
    }

    public static string ShortenPathForDisplay(string rawPath)
    {
        var normalized = rawPath.Replace('\\', '/').TrimEnd('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2)
        {
            return normalized;
        }
        return $"{parts[^2]}/{parts[^1]}";
    }

    public static string? TryExtractJsStringProperty(string raw, string propertyName)
    {
        var token = propertyName + ":";
        var idx = raw.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0)
        {
            token = "\"" + propertyName + "\":";
            idx = raw.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }
        }

        var cursor = idx + token.Length;
        while (cursor < raw.Length && char.IsWhiteSpace(raw[cursor]))
        {
            cursor++;
        }

        if (cursor >= raw.Length || (raw[cursor] != '"' && raw[cursor] != '\'' && raw[cursor] != '`'))
        {
            return null;
        }

        var quote = raw[cursor];
        cursor++;
        var sb = new StringBuilder();
        while (cursor < raw.Length)
        {
            var ch = raw[cursor++];
            if (ch == '\\' && cursor < raw.Length)
            {
                var next = raw[cursor++];
                sb.Append(next switch
                {
                    'n' => ' ',
                    'r' => ' ',
                    't' => ' ',
                    _ => next
                });
                continue;
            }
            if (ch == quote)
            {
                break;
            }
            sb.Append(ch);
        }

        var extracted = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(extracted) ? null : extracted;
    }
}
