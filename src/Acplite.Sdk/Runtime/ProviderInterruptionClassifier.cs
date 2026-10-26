using System.Globalization;
using System.Text.RegularExpressions;
using Acplite.Sdk.Protocol;

namespace Acplite.Sdk.Runtime;

/// <summary>
/// Normalizes provider error strings, JSON-RPC error payloads, and stderr diagnostics across
/// Claude Code, OpenAI Codex, Grok, and Google Antigravity into actionable <see cref="AcpInterruptionClassification"/>s.
/// </summary>
public static partial class ProviderInterruptionClassifier
{
    [GeneratedRegex(@"retry\s+after\s+(\d+)\s*(?:s|sec|seconds)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetryAfterSecondsRegex();

    [GeneratedRegex(@"resets?\s+(?:at\s+)?(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2}))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsoResetTimestampRegex();

    public static AcpInterruptionClassification Classify(
        string? message,
        string? rawCode = null,
        int? rpcCode = null,
        int retryAttempt = 0)
    {
        var text = (message ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();
        var codeLower = (rawCode ?? string.Empty).ToLowerInvariant();

        if (lower.Contains("context_length_exceeded") ||
            lower.Contains("prompt is too long") ||
            lower.Contains("maximum context length") ||
            lower.Contains("context window exceeded") ||
            codeLower.Contains("context_length_exceeded"))
        {
            return new AcpInterruptionClassification(
                Kind: AcpInterruptionKind.ContextWindowExceeded,
                IsRetryable: false,
                Message: string.IsNullOrEmpty(text) ? "Context window exceeded." : text,
                RawCode: rawCode ?? "context_length_exceeded");
        }

        if (lower.Contains("not logged in") ||
            lower.Contains("invalid api key") ||
            lower.Contains("authentication_error") ||
            lower.Contains("unauthorized") ||
            lower.Contains("please run `claude login`") ||
            lower.Contains("please run `codex login`") ||
            codeLower is "401" or "unauthorized" or "authentication_error")
        {
            return new AcpInterruptionClassification(
                Kind: AcpInterruptionKind.AuthenticationRequired,
                IsRetryable: false,
                Message: string.IsNullOrEmpty(text) ? "Authentication required." : text,
                RawCode: rawCode ?? "authentication_required");
        }

        if (lower.Contains("usage limit") ||
            lower.Contains("weekly limit") ||
            lower.Contains("5-hour limit") ||
            lower.Contains("insufficient_quota") ||
            lower.Contains("out of credits") ||
            lower.Contains("billing hard limit") ||
            lower.Contains("you've hit your limit") ||
            codeLower is "insufficient_quota" or "quota_exceeded" or "usage_limit_reached")
        {
            DateTimeOffset? resetsAt = null;
            var isoMatch = IsoResetTimestampRegex().Match(text);
            if (isoMatch.Success && DateTimeOffset.TryParse(isoMatch.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedReset))
            {
                resetsAt = parsedReset;
            }

            return new AcpInterruptionClassification(
                Kind: AcpInterruptionKind.RateLimitQuota,
                IsRetryable: false,
                Message: string.IsNullOrEmpty(text) ? "Account usage quota or rate-limit window reached." : text,
                RawCode: rawCode ?? "quota_exceeded",
                QuotaResetsAt: resetsAt);
        }

        if (lower.Contains("overloaded") ||
            lower.Contains("529") ||
            lower.Contains("rate_limit_error") ||
            lower.Contains("too many requests") ||
            lower.Contains("server_is_overloaded") ||
            lower.Contains("temporarily unavailable") ||
            lower.Contains("capacity") ||
            codeLower is "529" or "429" or "overloaded_error" or "rate_limit_error")
        {
            TimeSpan delay;
            var retryMatch = RetryAfterSecondsRegex().Match(text);
            if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs))
            {
                delay = TimeSpan.FromSeconds(Math.Clamp(secs, 1, 120));
            }
            else
            {
                var expSeconds = Math.Min(30, Math.Pow(2, Math.Clamp(retryAttempt, 0, 5)) * 1.5);
                delay = TimeSpan.FromSeconds(expSeconds);
            }

            return new AcpInterruptionClassification(
                Kind: AcpInterruptionKind.TransientCapacity,
                IsRetryable: true,
                Message: string.IsNullOrEmpty(text) ? "Provider temporarily overloaded." : text,
                RawCode: rawCode ?? "overloaded_error",
                RecommendedRetryDelay: delay);
        }

        if (lower.Contains("permission denied") ||
            lower.Contains("rejected by user") ||
            codeLower is "403" or "permission_denied")
        {
            return new AcpInterruptionClassification(
                Kind: AcpInterruptionKind.PermissionDenied,
                IsRetryable: false,
                Message: string.IsNullOrEmpty(text) ? "Permission denied." : text,
                RawCode: rawCode ?? "permission_denied");
        }

        if (lower.Contains("exited unexpectedly") ||
            lower.Contains("stream closed") ||
            codeLower is "process_crashed")
        {
            return new AcpInterruptionClassification(
                Kind: AcpInterruptionKind.ProcessCrashed,
                IsRetryable: true,
                Message: string.IsNullOrEmpty(text) ? "Agent process exited unexpectedly." : text,
                RawCode: rawCode ?? "process_crashed",
                RecommendedRetryDelay: TimeSpan.FromSeconds(2));
        }

        return new AcpInterruptionClassification(
            Kind: AcpInterruptionKind.UnknownError,
            IsRetryable: false,
            Message: string.IsNullOrEmpty(text) ? "Unknown agent error." : text,
            RawCode: rawCode ?? rpcCode?.ToString(CultureInfo.InvariantCulture));
    }
}
