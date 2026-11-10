using System.Collections.Concurrent;
using System.Security.Cryptography;
using Quiz.Game;

namespace Quiz.Pairing;

public abstract record ClaimResult
{
    public sealed record Claimed(string ScreenConnectionId) : ClaimResult;

    /// <summary>
    /// Unknown, expired and already claimed are deliberately one case: whoever probes for codes
    /// learns nothing from the answer.
    /// </summary>
    public sealed record NoSuchCode : ClaimResult;
}

/// <summary>
/// The pairing codes a television screen shows and a host's phone claims, keyed by their four-digit code.
/// A singleton shared by every connection. Codes live in their own map, so they cannot collide with room
/// codes even though both are drawn from 1000..9999. There is no timer: a code past its lifetime is refused
/// when someone asks for it, and <see cref="SweepExpired"/> (called on a schedule) frees the rest.
/// <see cref="Claim"/> is lock-free and single-use (an atomic remove); <see cref="Issue"/>,
/// <see cref="Forget"/> and <see cref="SweepExpired"/> share one lock so a screen's code replacement
/// and code uniqueness cannot be raced past.
/// </summary>
public sealed class PairingStore
{
    // Same 1000..9999 space as room codes. When it is crowded, a bounded number of draws gives up.
    private const int MaxCodeAttempts = 50;

    private readonly record struct Pairing(string ScreenConnectionId, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Pairing> _codes = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly PairingOptions _options;
    private readonly JoinThrottle _throttle;

    public PairingStore(TimeProvider clock, PairingOptions options)
    {
        _clock = clock;
        _options = options;
        _throttle = new JoinThrottle(clock, options.IssuesPerMinute);
    }

    /// <summary>
    /// Gives a screen a fresh code, replacing any it was holding. Returns null when the caller is throttled
    /// (its existing code is then left alone) or when no free code could be drawn.
    /// </summary>
    public string? Issue(string screenConnectionId, string clientKey)
    {
        lock (_gate)
        {
            if (!_throttle.Allow(clientKey)) return null;

            var now = _clock.GetUtcNow();
            for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
            {
                var code = RandomNumberGenerator.GetInt32(1000, 10000).ToString();
                // An expired code that nobody swept yet is as good as free.
                if (_codes.TryGetValue(code, out var existing) && existing.ExpiresAt > now) continue;

                RemoveHeldBy(screenConnectionId);
                _codes[code] = new Pairing(screenConnectionId, now + _options.Lifetime);
                return code;
            }

            return null;
        }
    }

    public ClaimResult Claim(string code)
    {
        if (code is null || !_codes.TryRemove(code, out var pairing)) return new ClaimResult.NoSuchCode();
        return pairing.ExpiresAt > _clock.GetUtcNow()
            ? new ClaimResult.Claimed(pairing.ScreenConnectionId)
            : new ClaimResult.NoSuchCode();
    }

    /// <summary>Drops the code a screen was holding, e.g. when its connection ends.</summary>
    public void Forget(string screenConnectionId)
    {
        lock (_gate) RemoveHeldBy(screenConnectionId);
    }

    /// <summary>Removes codes past their lifetime and returns how many there were. Called on a schedule.</summary>
    public int SweepExpired()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var removed = 0;
            foreach (var (code, pairing) in _codes)
            {
                if (pairing.ExpiresAt <= now && _codes.TryRemove(code, out _)) removed++;
            }
            return removed;
        }
    }

    // Caller holds _gate. At most 9000 entries, so a scan is cheaper than keeping a second map in step.
    private void RemoveHeldBy(string screenConnectionId)
    {
        foreach (var (code, pairing) in _codes)
        {
            if (pairing.ScreenConnectionId == screenConnectionId) _codes.TryRemove(code, out _);
        }
    }
}
