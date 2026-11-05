using FlowPay.Application.Outbox;
using Xunit;

namespace FlowPay.UnitTests.Application;

public class OutboxBackoffTests
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    [Fact]
    public void The_delay_grows_with_each_attempt()
    {
        TimeSpan first = OutboxBackoff.Compute(
            1, BaseDelay, MaxDelay, jitterFactor: 0d);

        TimeSpan second = OutboxBackoff.Compute(
            2, BaseDelay, MaxDelay, jitterFactor: 0d);

        TimeSpan third = OutboxBackoff.Compute(
            3, BaseDelay, MaxDelay, jitterFactor: 0d);

        Assert.Equal(TimeSpan.FromSeconds(1), first);
        Assert.Equal(TimeSpan.FromSeconds(2), second);
        Assert.Equal(TimeSpan.FromSeconds(4), third);
    }

    [Fact]
    public void The_delay_is_capped()
    {
        TimeSpan delay = OutboxBackoff.Compute(
            attempt: 30, BaseDelay, MaxDelay, jitterFactor: 0d);

        Assert.Equal(MaxDelay, delay);
    }

    /// <summary>
    /// A long failure history must not overflow the exponential term, which is why the
    /// exponent is clamped rather than fed straight into a shift.
    /// </summary>
    [Fact]
    public void A_very_high_attempt_count_does_not_overflow()
    {
        TimeSpan delay = OutboxBackoff.Compute(
            attempt: int.MaxValue, BaseDelay, MaxDelay, jitterFactor: 0.5d);

        Assert.InRange(delay, TimeSpan.Zero, MaxDelay);
    }

    [Fact]
    public void Attempt_zero_schedules_no_delay()
        => Assert.Equal(
            TimeSpan.Zero,
            OutboxBackoff.Compute(0, BaseDelay, MaxDelay, 0.5d));

    /// <summary>
    /// Jitter is what stops a broker outage from scheduling every pending row for the
    /// same instant and hitting the recovered broker with the whole backlog at once.
    /// </summary>
    [Fact]
    public void Jitter_spreads_retries_across_the_window()
    {
        HashSet<long> observed = [];

        for (int i = 0; i < 200; i++)
        {
            observed.Add(OutboxBackoff
                .Compute(3, BaseDelay, MaxDelay, jitterFactor: 0.5d)
                .Ticks);
        }

        // With full jitter the delays must not collapse onto a single value.
        Assert.True(observed.Count > 100);
    }

    [Fact]
    public void Jitter_never_pushes_the_delay_past_the_cap()
    {
        for (int attempt = 1; attempt <= 20; attempt++)
        {
            TimeSpan delay = OutboxBackoff.Compute(
                attempt, BaseDelay, MaxDelay, jitterFactor: 1d);

            Assert.InRange(delay, TimeSpan.Zero, MaxDelay);
        }
    }
}
