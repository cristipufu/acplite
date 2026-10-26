namespace Acplite.Sdk.Formatting;

/// <summary>
/// Pure helper methods for normalizing single-line session titles, extracting user requests,
/// and inferring tool categories without duplicating string logic across adapters.
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
        if (lower.Contains("bash") || lower.Contains("command") || lower.Contains("exec"))
        {
            return "execute";
        }
        if (lower.Contains("edit") || lower.Contains("write") || lower.Contains("patch") || lower.Contains("filechange"))
        {
            return "edit";
        }
        if (lower.Contains("read") || lower.Contains("glob") || lower.Contains("grep") || lower.Contains("search"))
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
}
