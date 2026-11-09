namespace Quiz.Game;

/// <summary>
/// Sliding-window limit on join attempts per client key: at most N allowed attempts in any
/// trailing minute. Refused attempts are not recorded, so hammering does not extend the lockout.
/// An attempt made exactly one minute after an earlier one no longer counts it.
/// </summary>
public sealed class JoinThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly int _attemptsPerMinute;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);
    private DateTimeOffset _lastPrune;

    public JoinThrottle(TimeProvider clock, int attemptsPerMinute)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsPerMinute, 1);
        _clock = clock;
        _attemptsPerMinute = attemptsPerMinute;
        _lastPrune = clock.GetUtcNow();
    }

    public bool Allow(string clientKey)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            PruneAll(now);

            if (!_attempts.TryGetValue(clientKey, out var queue))
                _attempts[clientKey] = queue = new Queue<DateTimeOffset>();

            Expire(queue, now);
            if (queue.Count >= _attemptsPerMinute) return false;

            queue.Enqueue(now);
            return true;
        }
    }

    private static void Expire(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= Window) queue.Dequeue();
    }

    // Drops clients with nothing left in the window, at most once a window, so the table cannot grow without bound.
    private void PruneAll(DateTimeOffset now)
    {
        if (now - _lastPrune < Window) return;
        _lastPrune = now;
        foreach (var (key, queue) in _attempts.ToList())
        {
            Expire(queue, now);
            if (queue.Count == 0) _attempts.Remove(key);
        }
    }
}
