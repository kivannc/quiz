using System.Collections.Concurrent;

namespace Quiz.Hubs;

/// <summary>
/// Which room each connection's groups point at: one entry for the seat it plays from, one for the screen
/// it hosts with. SignalR puts a connection into a group and never takes it out again on its own, so a TV
/// that opens a second room — or a phone that joins one — would go on hearing the room it left, and be
/// repainted by it. This is the memory <see cref="GameHub"/> needs to leave the old groups first.
///
/// It mirrors the groups and decides nothing: a missing entry only ever means there is no old group to
/// leave, which is also what every connection starts out as.
/// </summary>
public sealed class ConnectionRooms
{
    private readonly ConcurrentDictionary<string, string> _playing = new();
    private readonly ConcurrentDictionary<string, string> _hosting = new();

    /// <summary>The room this connection is seated in, if it is seated in one.</summary>
    public string? Playing(string connectionId) => _playing.GetValueOrDefault(connectionId);

    /// <summary>The room this connection is the host screen of, if it is one.</summary>
    public string? Hosting(string connectionId) => _hosting.GetValueOrDefault(connectionId);

    /// <summary>Seats this connection in <paramref name="code"/> and returns the room it was seated in, if that was another one.</summary>
    public string? Play(string connectionId, string code) => Move(_playing, connectionId, code);

    /// <summary>Makes this connection <paramref name="code"/>'s host screen and returns the room it hosted, if that was another one.</summary>
    public string? Host(string connectionId, string code) => Move(_hosting, connectionId, code);

    /// <summary>Drops a connection that has gone. SignalR has already dropped its groups; this is only the mirror of them.</summary>
    public void Forget(string connectionId)
    {
        _playing.TryRemove(connectionId, out _);
        _hosting.TryRemove(connectionId, out _);
    }

    private static string? Move(ConcurrentDictionary<string, string> rooms, string connectionId, string code)
    {
        var left = rooms.GetValueOrDefault(connectionId);
        rooms[connectionId] = code;
        // The same room again is not a move: the caller re-joining its own groups has nothing to leave.
        return left == code ? null : left;
    }
}
