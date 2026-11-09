using Quiz.Game;

namespace Quiz.Tests;

public sealed class ScoringTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(true,  0,    1000)]   // instant
    [InlineData(true,  10,   750)]    // halfway through a 20s limit
    [InlineData(true,  20,   500)]    // on the buzzer
    [InlineData(true,  25,   0)]      // after the limit
    [InlineData(false, 0,    0)]      // wrong, however fast
    public void Points_decays_from_1000_to_500_across_the_limit(bool correct, int elapsedSeconds, int expected)
    {
        Assert.Equal(expected, Scoring.Points(correct, TimeSpan.FromSeconds(elapsedSeconds), Limit));
    }

    [Fact]
    public void Points_is_zero_for_a_negative_elapsed_time()
    {
        Assert.Equal(0, Scoring.Points(true, TimeSpan.FromSeconds(-1), Limit));
    }

    [Fact]
    public void Points_rounds_half_away_from_zero()
    {
        // 1000 - 500 * 0.06 / 20 = 998.5: away from zero gives 999, banker's rounding gives 998.
        Assert.Equal(999, Scoring.Points(true, TimeSpan.FromMilliseconds(60), Limit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Points_throws_for_a_non_positive_limit(int limitSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Scoring.Points(true, TimeSpan.Zero, TimeSpan.FromSeconds(limitSeconds)));
    }
}
