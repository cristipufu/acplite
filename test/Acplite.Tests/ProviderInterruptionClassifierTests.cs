using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;

namespace Acplite.Tests;

public sealed class ProviderInterruptionClassifierTests
{
    [Fact]
    public void Classifies_claude_529_overloaded_as_retryable_transient_capacity()
    {
        var result = ProviderInterruptionClassifier.Classify(
            "API Error: 529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}} retry after 7 seconds",
            rawCode: "529");

        Assert.Equal(AcpInterruptionKind.TransientCapacity, result.Kind);
        Assert.True(result.IsRetryable);
        Assert.Equal(TimeSpan.FromSeconds(7), result.RecommendedRetryDelay);
    }

    [Fact]
    public void Classifies_transient_capacity_with_exponential_backoff_when_no_retry_after_header()
    {
        var attempt0 = ProviderInterruptionClassifier.Classify("Server temporarily unavailable (429)", rawCode: "429", retryAttempt: 0);
        var attempt2 = ProviderInterruptionClassifier.Classify("Server temporarily unavailable (429)", rawCode: "429", retryAttempt: 2);

        Assert.Equal(AcpInterruptionKind.TransientCapacity, attempt0.Kind);
        Assert.True(attempt0.IsRetryable);
        Assert.NotNull(attempt0.RecommendedRetryDelay);
        Assert.NotNull(attempt2.RecommendedRetryDelay);
        Assert.True(attempt2.RecommendedRetryDelay > attempt0.RecommendedRetryDelay);
    }

    [Fact]
    public void Classifies_weekly_quota_with_reset_timestamp()
    {
        var result = ProviderInterruptionClassifier.Classify(
            "You've hit your weekly limit. Quota resets at 2026-10-30T18:00:00Z.",
            rawCode: "usage_limit_reached");

        Assert.Equal(AcpInterruptionKind.RateLimitQuota, result.Kind);
        Assert.False(result.IsRetryable);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T18:00:00Z"), result.QuotaResetsAt);
    }

    [Fact]
    public void Classifies_context_window_exceeded()
    {
        var result = ProviderInterruptionClassifier.Classify(
            "Prompt is too long: maximum context length is 200000 tokens.",
            rawCode: "context_length_exceeded");

        Assert.Equal(AcpInterruptionKind.ContextWindowExceeded, result.Kind);
        Assert.False(result.IsRetryable);
    }

    [Fact]
    public void Classifies_authentication_required()
    {
        var result = ProviderInterruptionClassifier.Classify(
            "Not logged in. Please run `claude login` to authenticate.");

        Assert.Equal(AcpInterruptionKind.AuthenticationRequired, result.Kind);
        Assert.False(result.IsRetryable);
    }

    [Fact]
    public void Classifies_permission_denied_and_process_crashed_and_unknown_errors()
    {
        var denied = ProviderInterruptionClassifier.Classify("Tool execution rejected by user", rawCode: "403");
        Assert.Equal(AcpInterruptionKind.PermissionDenied, denied.Kind);
        Assert.False(denied.IsRetryable);

        var crashed = ProviderInterruptionClassifier.Classify("Agent process exited unexpectedly with code 137.", rawCode: "process_crashed");
        Assert.Equal(AcpInterruptionKind.ProcessCrashed, crashed.Kind);
        Assert.True(crashed.IsRetryable);

        var unknown = ProviderInterruptionClassifier.Classify("Something unexpected happened", rpcCode: -32603);
        Assert.Equal(AcpInterruptionKind.UnknownError, unknown.Kind);
        Assert.False(unknown.IsRetryable);
        Assert.Equal("-32603", unknown.RawCode);
    }
}
