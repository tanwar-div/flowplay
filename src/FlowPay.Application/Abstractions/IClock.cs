namespace FlowPay.Application.Abstractions;

/// <summary>
/// The current time, injected rather than read from <see cref="DateTimeOffset.UtcNow"/>.
/// </summary>
/// <remarks>
/// Authorization expiry, retry backoff windows and idempotency TTLs are all time-driven.
/// Injecting the clock is what lets the test suite drive a seven-day authorization window
/// to its edge in a millisecond instead of approximating it.
/// </remarks>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The production clock.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
