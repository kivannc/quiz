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
}
