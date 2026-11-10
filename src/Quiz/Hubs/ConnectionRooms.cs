using System.Collections.Concurrent;

namespace Quiz.Hubs;

/// <summary>
/// Which room each connection's groups point at: one entry for the seat it plays from, one for the screen
/// it hosts with. SignalR puts a connection into a group and never takes it out again on its own, so a TV
/// that opens a second room — or a phone that joins one — would go on hearing the room it left, and be
/// repainted by it. This is the memory <see cref="GameHub"/> needs to leave the old groups first.
///
/// Three parts of it are not a mirror and do decide something. A console that has claimed a pairing code
/// remembers the screen it claimed until it opens a room, and a missing entry there means "no screen":
/// <c>CreateRoom</c> refuses without one. <see cref="Pair"/> enforces that a screen answers to one console
/// and that a gone screen cannot be claimed, which is why it also keeps the set of connections that are up
/// under the same lock. And <see cref="Display"/> remembers which screen each open room is shown on, past
/// the moment the claim is spent: that is the only thing that relates a room back to its television, so it
/// is what notices one leaving. The room entries (playing, hosting) remain a plain mirror: a missing one
/// only means there is no old group to leave, which is also what every connection starts out as.
/// </summary>
public sealed class ConnectionRooms
{
    private readonly ConcurrentDictionary<string, string> _playing = new();
    private readonly ConcurrentDictionary<string, string> _hosting = new();

    // console connection -> the screen connection it has claimed. Its writers take _screenGate, because
    // "one screen has at most one console" spans two entries and cannot be a single atomic dictionary call.
    private readonly Dictionary<string, string> _screens = new(StringComparer.Ordinal);
    private readonly object _screenGate = new();

    // Connections that are up, kept only so a claim cannot be recorded for a screen that has already gone:
    // without it, a screen disconnecting between "claimed" and "paired" would leave the console holding a
    // television that no longer exists. Guarded by _screenGate, like the links it protects.
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);

    // room code -> the screen connection that room is shown on. The claim above is spent the moment the
    // room opens, so this is what outlives it: without it nothing relates a room back to its television,
    // and a room whose display has gone goes on being driven blind. Guarded by _screenGate too, because
    // forgetting a room reads it and drops the claims that name the screen it finds.
    private readonly Dictionary<string, string> _displays = new(StringComparer.Ordinal);

    /// <summary>The room this connection is seated in, if it is seated in one.</summary>
    public string? Playing(string connectionId) => _playing.GetValueOrDefault(connectionId);

    /// <summary>The room this connection is the host screen of, if it is one.</summary>
    public string? Hosting(string connectionId) => _hosting.GetValueOrDefault(connectionId);

    /// <summary>Seats this connection in <paramref name="code"/> and returns the room it was seated in, if that was another one.</summary>
    public string? Play(string connectionId, string code) => Move(_playing, connectionId, code);

    /// <summary>Makes this connection <paramref name="code"/>'s host screen and returns the room it hosted, if that was another one.</summary>
    public string? Host(string connectionId, string code) => Move(_hosting, connectionId, code);

    /// <summary>
    /// Forgets that a connection was the host screen of <paramref name="code"/>, if it still is.
    /// For a television another one has replaced as a room's display: it is being taken out of that
    /// room's groups, and the mirror of them has to agree, or the next room this screen is seated
    /// into would leave the groups of a room it is no longer in and keep its own.
    /// </summary>
    public void Unhost(string connectionId, string code) =>
        _hosting.TryRemove(new KeyValuePair<string, string>(connectionId, code));

    /// <summary>Notes that a connection is up. <see cref="Forget"/> is its counterpart.</summary>
    public void Connected(string connectionId)
    {
        lock (_screenGate) _live.Add(connectionId);
    }

    /// <summary>Whether the connection is up, as far as <see cref="Connected"/> and <see cref="Forget"/> know.</summary>
    public bool IsConnected(string connectionId)
    {
        lock (_screenGate) return _live.Contains(connectionId);
    }

    /// <summary>The screen this console has claimed and not yet opened a room on, if any.</summary>
    public string? Screen(string consoleConnectionId)
    {
        lock (_screenGate) return _screens.GetValueOrDefault(consoleConnectionId);
    }

    /// <summary>
    /// Records that a console has claimed a screen, replacing any screen it held before. A screen answers
    /// to one console: if another console held this one, that claim is dropped, so two phones can never
    /// both be driving the same television. Returns false, and records nothing, if the screen has
    /// already disconnected.
    /// </summary>
    public bool Pair(string consoleConnectionId, string screenConnectionId)
    {
        lock (_screenGate)
        {
            if (!_live.Contains(screenConnectionId)) return false;

            foreach (var (console, screen) in _screens.ToList())
            {
                if (screen == screenConnectionId) _screens.Remove(console);
            }
            _screens[consoleConnectionId] = screenConnectionId;
            return true;
        }
    }

    /// <summary>Forgets the console's claim, which has been used, and returns the screen it named.</summary>
    public string? Unpair(string consoleConnectionId)
    {
        lock (_screenGate) return _screens.Remove(consoleConnectionId, out var screen) ? screen : null;
    }

    /// <summary>The screen connection the room is shown on, or null when no television is in it.</summary>
    public string? DisplayOf(string code)
    {
        lock (_screenGate) return _displays.GetValueOrDefault(code);
    }

    /// <summary>
    /// Records which screen a room is shown on, replacing whatever was there — a room opening on a
    /// screen, and a screen being seated back into a room it fell out of. A television shows one room:
    /// any other room this screen was the display of is dropped, so the room a gone screen leaves
    /// without one is never ambiguous.
    ///
    /// Returns false, and records nothing, if the screen has already disconnected — the same rule
    /// <see cref="Pair"/> holds, and for a heavier reason: an entry naming a connection that is gone
    /// is one <see cref="Forget"/> will never match again, so nothing would ever notice that room
    /// losing its television, and the console would carry the round on with no display in the room
    /// at all. A caller that is refused has to say so rather than leave a console believing in a
    /// screen the room does not have.
    ///
    /// <paramref name="replaced"/> is the screen that was showing this room and has just lost it:
    /// another television claimed into a live room, with the first still in the room's groups and
    /// still painting the game. Taking it out of them is the caller's part, and this is the only
    /// thing that names the connection to take out. Null when the room had no display, and when the
    /// same screen is being seated into it again. It is always a connection that is up: the guard
    /// above records no other kind, and <see cref="Forget"/> takes the entry out with the screen.
    /// </summary>
    public bool Display(string code, string screenConnectionId, out string? replaced)
    {
        lock (_screenGate)
        {
            replaced = null;
            if (!_live.Contains(screenConnectionId)) return false;

            foreach (var (room, screen) in _displays.ToList())
            {
                if (screen == screenConnectionId) _displays.Remove(room);
            }
            // After the loop above, so re-seating the room's own screen reports nothing replaced.
            if (_displays.TryGetValue(code, out var was)) replaced = was;
            _displays[code] = screenConnectionId;
            return true;
        }
    }

    /// <summary>
    /// Forgets a room that no longer exists and returns every connection that was pointing at it, as a
    /// host screen or a seat or both, so its groups can be emptied too. An entry that moved to another
    /// room between the read and the removal is left alone: it is already somewhere else.
    /// </summary>
    public IReadOnlyCollection<string> ForgetRoom(string code)
    {
        // Its television is going back to showing a pairing code, so a claim anybody still holds on that
        // screen is a claim on something that is asking to be paired again — the re-seat branch of
        // ClaimScreen leaves exactly that behind. Dropped here, or the console would read as holding a
        // screen at the very moment the screen offers a fresh code and the console says to type it.
        lock (_screenGate)
        {
            if (_displays.Remove(code, out var display))
            {
                foreach (var (console, screen) in _screens.ToList())
                {
                    if (screen == display) _screens.Remove(console);
                }
            }
        }

        var gone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rooms in new[] { _playing, _hosting })
        {
            foreach (var (id, room) in rooms)
            {
                if (room == code && rooms.TryRemove(new KeyValuePair<string, string>(id, code))) gone.Add(id);
            }
        }
        return gone;
    }

    /// <summary>
    /// Drops a connection that has gone, and returns the room it was the display of, if it was one.
    /// SignalR has already dropped its groups, so most of this is only the mirror of them — but a room
    /// whose television has just left has to be told, and this is the only place that knows.
    /// </summary>
    public string? Forget(string connectionId)
    {
        _playing.TryRemove(connectionId, out _);
        _hosting.TryRemove(connectionId, out _);

        // From either side: a console that is gone has no claim, and a screen that is gone cannot be driven.
        lock (_screenGate)
        {
            _live.Remove(connectionId);
            _screens.Remove(connectionId);
            foreach (var (console, screen) in _screens.ToList())
            {
                if (screen == connectionId) _screens.Remove(console);
            }

            string? blinded = null;
            foreach (var (room, screen) in _displays)
            {
                if (screen != connectionId) continue;
                blinded = room;
                break; // a television shows one room, which Display is what keeps true
            }
            if (blinded is not null) _displays.Remove(blinded);
            return blinded;
        }
    }

    private static string? Move(ConcurrentDictionary<string, string> rooms, string connectionId, string code)
    {
        var left = rooms.GetValueOrDefault(connectionId);
        rooms[connectionId] = code;
        // The same room again is not a move: the caller re-joining its own groups has nothing to leave.
        return left == code ? null : left;
    }
}
