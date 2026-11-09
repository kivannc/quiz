using Microsoft.Extensions.Time.Testing;
using Quiz.Game;

namespace Quiz.Tests;

public sealed class JoinThrottleTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Allow_permits_up_to_the_limit_then_refuses()
    {
        var throttle = new JoinThrottle(_clock, 3);

        Assert.True(throttle.Allow("1.2.3.4"));
        Assert.True(throttle.Allow("1.2.3.4"));
        Assert.True(throttle.Allow("1.2.3.4"));
        Assert.False(throttle.Allow("1.2.3.4"));
        Assert.False(throttle.Allow("1.2.3.4"));
    }

    [Fact]
    public void Allow_permits_again_once_the_window_has_passed()
    {
        var throttle = new JoinThrottle(_clock, 2);
        throttle.Allow("a");
        throttle.Allow("a");
        Assert.False(throttle.Allow("a"));

        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(throttle.Allow("a"));

        _clock.Advance(TimeSpan.FromSeconds(1));   // a full minute since the first attempts
        Assert.True(throttle.Allow("a"));
    }

    [Fact]
    public void Refused_attempts_do_not_extend_the_lockout()
    {
        var throttle = new JoinThrottle(_clock, 1);
        Assert.True(throttle.Allow("a"));

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(throttle.Allow("a"));

        _clock.Advance(TimeSpan.FromSeconds(30));  // 60s after the one allowed attempt
        Assert.True(throttle.Allow("a"));
    }

    [Fact]
    public void Clients_are_throttled_independently()
    {
        var throttle = new JoinThrottle(_clock, 1);

        Assert.True(throttle.Allow("a"));
        Assert.False(throttle.Allow("a"));
        Assert.True(throttle.Allow("b"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_a_non_positive_limit(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JoinThrottle(_clock, limit));
    }
}
