namespace Quiz.Pairing;

/// <summary>
/// How long a pairing code lives and how often one client may ask for a new one.
/// Validates itself and is immutable (no <c>with</c>, no setters), like <see cref="Game.RoomOptions"/>:
/// <see cref="PairingStore"/> trusts these values, so there must be no path to an invalid instance.
/// </summary>
public sealed class PairingOptions
{
    public PairingOptions(TimeSpan lifetime, int issuesPerMinute)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(issuesPerMinute, 1);

        Lifetime = lifetime;
        IssuesPerMinute = issuesPerMinute;
    }

    public TimeSpan Lifetime { get; }
    public int IssuesPerMinute { get; }
}
