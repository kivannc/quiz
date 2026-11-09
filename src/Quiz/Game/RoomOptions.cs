namespace Quiz.Game;

/// <summary>
/// The limits that keep a public game server from being turned into an open door.
/// Validates itself: these values come from deployment configuration, and <see cref="Room"/>
/// trusts them, so a bad value must fail loudly here instead of starting a broken game.
/// </summary>
public sealed record RoomOptions
{
    public RoomOptions(int MaxRooms, int MaxPlayers, TimeSpan IdleTimeout, TimeSpan QuestionLimit,
                       int JoinAttemptsPerMinute)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRooms, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPlayers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(QuestionLimit, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(JoinAttemptsPerMinute, 1);

        this.MaxRooms = MaxRooms;
        this.MaxPlayers = MaxPlayers;
        this.IdleTimeout = IdleTimeout;
        this.QuestionLimit = QuestionLimit;
        this.JoinAttemptsPerMinute = JoinAttemptsPerMinute;
    }

    public int MaxRooms { get; init; }
    public int MaxPlayers { get; init; }
    public TimeSpan IdleTimeout { get; init; }
    public TimeSpan QuestionLimit { get; init; }
    public int JoinAttemptsPerMinute { get; init; }
}
