using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Quiz.Game;

public abstract record CreateResult
{
    public sealed record Created(string Code) : CreateResult;
    public sealed record TooManyRooms : CreateResult;
    public sealed record UnknownSet : CreateResult;
}

/// <summary>
/// All live rooms, keyed by their four-digit code. A singleton shared by every connection.
/// Reads (<see cref="Find"/>, <see cref="Count"/>) are lock-free; <see cref="Create"/> and
/// <see cref="SweepIdle"/> share one lock so the room cap and code uniqueness cannot be raced past.
/// </summary>
public sealed class RoomStore(IQuestionSetSource sets, TimeProvider clock, RoomOptions options)
{
    // Codes are 1000..9999 (9000 of them). When the space is crowded, a bounded number of
    // random draws gives up as TooManyRooms rather than looping.
    private const int MaxCodeAttempts = 50;

    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public int Count => _rooms.Count;

    public CreateResult Create(string setId)
    {
        var set = sets.ById(setId);
        if (set is null) return new CreateResult.UnknownSet();

        lock (_gate)
        {
            if (_rooms.Count >= options.MaxRooms) return new CreateResult.TooManyRooms();

            for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
            {
                var code = RandomNumberGenerator.GetInt32(1000, 10000).ToString();
                if (_rooms.ContainsKey(code)) continue;

                var room = new Room(code, set, clock, options.MaxPlayers, options.QuestionLimit);
                _rooms[code] = room;
                return new CreateResult.Created(code);
            }

            return new CreateResult.TooManyRooms();
        }
    }

    public Room? Find(string code) => code is not null && _rooms.TryGetValue(code, out var room) ? room : null;

    /// <summary>
    /// Takes a room out of the store and frees its code, which may then be handed to a new room.
    /// Returns whether it was still there. Lock-free like <see cref="Find"/>: removing one room
    /// races with nothing the cap or the code draw depend on.
    /// </summary>
    public bool Remove(string code) => code is not null && _rooms.TryRemove(code, out _);

    /// <summary>
    /// Removes rooms with no activity for longer than the idle timeout and returns their codes, so that
    /// whoever called can end them the way <c>CloseRoom</c> does: a room that goes without telling anyone
    /// leaves phones listening to a code that is free to be drawn again. Called on a schedule.
    /// </summary>
    public IReadOnlyList<string> SweepIdle()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            var removed = new List<string>();
            foreach (var (code, room) in _rooms)
            {
                if (now - room.LastActivity > options.IdleTimeout && _rooms.TryRemove(code, out _)) removed.Add(code);
            }
            return removed;
        }
    }
}
