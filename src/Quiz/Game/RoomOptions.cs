namespace Quiz.Game;

/// <summary>
/// The limits that keep a public game server from being turned into an open door.
/// Validates itself and is immutable (no <c>with</c>, no setters): these values come from deployment
/// configuration and <see cref="Room"/> trusts them, so there must be no path to an invalid instance.
/// </summary>
public sealed class RoomOptions
{
    /// <summary>Room codes are four digits, 1000..9999.</summary>
    public const int CodeSpace = 9000;

    public RoomOptions(int maxRooms, int maxPlayers, TimeSpan idleTimeout, TimeSpan questionLimit,
                       int joinAttemptsPerMinute)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRooms, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRooms, CodeSpace);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPlayers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(questionLimit, TimeSpan.Zero);
        // A room idle for a whole question limit must not be swept mid-question.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, questionLimit);
        ArgumentOutOfRangeException.ThrowIfLessThan(joinAttemptsPerMinute, 1);

        MaxRooms = maxRooms;
        MaxPlayers = maxPlayers;
        IdleTimeout = idleTimeout;
        QuestionLimit = questionLimit;
        JoinAttemptsPerMinute = joinAttemptsPerMinute;
    }

    public int MaxRooms { get; }
    public int MaxPlayers { get; }
    public TimeSpan IdleTimeout { get; }
    public TimeSpan QuestionLimit { get; }
    public int JoinAttemptsPerMinute { get; }
}
