namespace Quiz.Game;

/// <summary>Speed-weighted points for one answer: a correct answer decays linearly from MaxPoints to MinPoints over the limit.</summary>
public static class Scoring
{
    public const int MaxPoints = 1000;
    public const int MinPoints = 500;

    /// <summary>
    /// Points for an answer given <paramref name="elapsed"/> after the question opened.
    /// Wrong, missing, negative-elapsed or late (after <paramref name="limit"/>) answers score 0.
    /// </summary>
    public static int Points(bool correct, TimeSpan elapsed, TimeSpan limit)
    {
        if (limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The limit must be positive.");
        }

        if (!correct || elapsed < TimeSpan.Zero || elapsed > limit)
        {
            return 0;
        }

        var fraction = elapsed.Ticks / (double)limit.Ticks;
        var points = MaxPoints - (MaxPoints - MinPoints) * fraction;
        return Math.Clamp((int)Math.Round(points, MidpointRounding.AwayFromZero), MinPoints, MaxPoints);
    }
}
