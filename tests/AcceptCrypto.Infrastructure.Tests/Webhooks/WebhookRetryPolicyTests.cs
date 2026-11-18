using AcceptCrypto.Infrastructure.Webhooks;

namespace AcceptCrypto.Infrastructure.Tests.Webhooks;

/// <summary>FR-14: exponential backoff with jitter, attempt limit and total window.</summary>
public sealed class WebhookRetryPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Default_schedule_doubles_from_30_s_caps_at_1_h_and_spans_about_24_h()
    {
        var delays = WebhookRetryPolicy.NominalDelays(new WebhookOptions());

        Assert.Equal(
            [30, 60, 120, 240, 480, 960, 1920, 3600, 3600],
            delays.Take(9).Select(d => (int)d.TotalSeconds));
        Assert.Equal(29, delays.Count); // 30 attempts
        var total = delays.Aggregate(TimeSpan.Zero, (a, d) => a + d);
        Assert.InRange(total, TimeSpan.FromHours(20), TimeSpan.FromHours(24));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(5, 480)]
    [InlineData(12, 3600)]
    public void Next_attempt_is_the_nominal_delay_with_plus_minus_20_percent_jitter(int failedAttempts, int nominalSeconds)
    {
        var options = new WebhookOptions();
        for (var i = 0; i < 200; i++)
        {
            var next = WebhookRetryPolicy.NextAttemptAt(failedAttempts, Start, Start, options);
            Assert.NotNull(next);
            var delay = (next.Value - Start).TotalSeconds;
            Assert.InRange(delay, nominalSeconds * 0.8 - 0.001, Math.Min(nominalSeconds * 1.2, options.MaxRetryDelay.TotalSeconds) + 0.001);
        }
    }

    [Fact]
    public void Gives_up_when_the_attempt_limit_is_reached()
    {
        var options = new WebhookOptions { MaxAttempts = 3 };

        Assert.NotNull(WebhookRetryPolicy.NextAttemptAt(2, Start, Start, options));
        Assert.Null(WebhookRetryPolicy.NextAttemptAt(3, Start, Start, options));
    }

    [Fact]
    public void Gives_up_when_the_next_attempt_would_fall_outside_the_window()
    {
        var options = new WebhookOptions { RetryWindow = TimeSpan.FromMinutes(10) };

        Assert.NotNull(WebhookRetryPolicy.NextAttemptAt(1, Start, Start.AddMinutes(5), options)); // ~30 s later: inside
        Assert.Null(WebhookRetryPolicy.NextAttemptAt(1, Start, Start.AddMinutes(9.9), options)); // would land after 10 min
        Assert.NotNull(WebhookRetryPolicy.NextAttemptAt(1, Start.AddMinutes(9.9), Start.AddMinutes(9.9), options)); // resend = new cycle
    }

    [Fact]
    public void Dispatcher_settings_are_validated()
    {
        Assert.True(new WebhookOptions().IsValid(out _));
        Assert.False(new WebhookOptions { ClaimLease = TimeSpan.FromSeconds(5) }.IsValid(out var lease));
        Assert.Contains("ClaimLease", lease, StringComparison.Ordinal);
        Assert.False(new WebhookOptions { BatchSize = 0 }.IsValid(out _));
        Assert.False(new WebhookOptions { MaxParallelDeliveries = 0 }.IsValid(out _));
        Assert.False(new WebhookOptions { PollInterval = TimeSpan.Zero }.IsValid(out _));
    }
}
