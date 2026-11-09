using Microsoft.AspNetCore.SignalR;
using Quiz.Game;

namespace Quiz.Hubs;

/// <summary>
/// The wire between the pages and <see cref="Room"/>. It authorizes, looks the room up, calls one method on it
/// and broadcasts the result; no game rule is decided here.
///
/// Groups: every participant is in group <c>code</c>; host screens are also in <c>code + ":host"</c>,
/// which is where <c>AnswerCount</c> and <c>RoomState</c> go. Every host action carries the key and re-joins
/// the caller to both groups, so a reloaded TV page resumes simply by re-sending it.
///
/// <see cref="Room"/> is strict about phases and throws when asked for something that makes no sense. A double-clicked
/// button or a reloaded page re-sending an action is normal here, so host actions check the phase first and quietly
/// ignore what does not apply. Failures a page can act on are <see cref="HubException"/>s with a short code.
/// </summary>
public sealed class GameHub(
    RoomStore rooms,
    IHostAuthorizer hosts,
    JoinThrottle joinThrottle,
    [FromKeyedServices(GameHub.CreateThrottleKey)] JoinThrottle createThrottle) : Hub
{
    public const string CreateThrottleKey = "create-room";

    private const int MaxNameLength = 24;

    private static string HostGroup(string code) => code + ":host";

    /// <summary>Opens a room and returns its code. Throws "throttled", "forbidden", "unknown-set" or "too-many-rooms".</summary>
    public async Task<string> CreateRoom(string hostKey, string setId)
    {
        // Counted before the key is checked, so guessing the key is what gets limited.
        if (!createThrottle.Allow(ClientKey())) throw new HubException("throttled");
        if (!hosts.IsHost(hostKey)) throw new HubException("forbidden");

        var code = rooms.Create(setId) switch
        {
            CreateResult.Created created => created.Code,
            CreateResult.TooManyRooms => throw new HubException("too-many-rooms"),
            _ => throw new HubException("unknown-set"),
        };

        await JoinHostGroups(code);
        return code;
    }

    /// <summary>Seats a player. Returns the <see cref="JoinResult"/> (camelCase on the wire); a refused join (RoomFull, RoundInProgress) is a result, not an error.</summary>
    public async Task<JoinResult> Join(string code, string name)
    {
        if (!joinThrottle.Allow(ClientKey())) throw new HubException("throttled");
        var room = RoomOrThrow(code);

        name = name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > MaxNameLength) throw new HubException("invalid-name");

        var result = room.Join(name, Context.ConnectionId);
        if (result is JoinResult.Joined or JoinResult.Rejoined)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, code);
            var seat = room.Players.First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            await Clients.Caller.SendAsync("Seated", seat.Name, seat.Score);
            await Resync(room);
            await SendRoomState(room);
        }
        return result;
    }

    public async Task StartRound(string code, string hostKey)
    {
        var room = HostRoom(code, hostKey);
        await JoinHostGroups(code);
        if (room.Phase != RoomPhase.Lobby || !Try(room.Start)) return;

        await AnnounceQuestion(room);
        await SendRoomState(room);
    }

    public async Task Answer(string code, Marker marker)
    {
        var room = RoomOrThrow(code);
        if (!room.Answer(Context.ConnectionId, marker)) return;

        await SendAnswerCount(room);
    }

    public async Task CloseQuestion(string code, string hostKey)
    {
        var room = HostRoom(code, hostKey);
        await JoinHostGroups(code);
        if (room.Phase != RoomPhase.Question) return;
        if (!Try(room.CloseQuestion, out var reveal)) return;

        await Clients.Group(code).SendAsync("Revealed", reveal);
        await SendRoomState(room);
    }

    public async Task AdvanceQuestion(string code, string hostKey)
    {
        var room = HostRoom(code, hostKey);
        await JoinHostGroups(code);
        if (room.Phase != RoomPhase.Reveal) return;
        if (!Try(room.Advance, out var another)) return;

        if (another)
        {
            await AnnounceQuestion(room);
        }
        else
        {
            await Clients.Group(code).SendAsync("RoundFinished", room.Standings);
        }
        await SendRoomState(room);
    }

    // Throttles are per client address, so opening a fresh connection buys no new allowance. Program.cs makes the
    // address the real client's behind the reverse proxy. Without an address (never expected) fall back to the connection.
    private string ClientKey() =>
        Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? Context.ConnectionId;

    // A phone that joins or rejoins mid-round is shown what is on screen now, to that connection only.
    private async Task Resync(Room room)
    {
        // Read-only accessors only: nothing on a player's code path may change the game. Each returns null if the
        // phase moved on since the room was looked up, in which case there is simply nothing to resync.
        if (room.CurrentReveal is { } reveal)
            await Clients.Caller.SendAsync("Revealed", reveal);
        else if (room.Phase == RoomPhase.Question && room.CurrentQuestion is { } question)
            await Clients.Caller.SendAsync("QuestionStarted", question);
    }

    private Room RoomOrThrow(string code) => rooms.Find(code) ?? throw new HubException("no-such-room");

    private Room HostRoom(string code, string hostKey)
    {
        if (!hosts.IsHost(hostKey)) throw new HubException("forbidden");
        return RoomOrThrow(code);
    }

    private async Task JoinHostGroups(string code)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, code);
        await Groups.AddToGroupAsync(Context.ConnectionId, HostGroup(code));
    }

    private async Task AnnounceQuestion(Room room)
    {
        await Clients.Group(room.Code).SendAsync("QuestionStarted", room.CurrentQuestion);
        await SendAnswerCount(room);
    }

    private Task SendAnswerCount(Room room) =>
        Clients.Group(HostGroup(room.Code)).SendAsync("AnswerCount", room.AnswerCount, room.Players.Count);

    private Task SendRoomState(Room room) =>
        Clients.Group(HostGroup(room.Code)).SendAsync("RoomState", room.Players, room.Phase.ToString().ToLowerInvariant());

    // The phase was checked a moment ago, but two host clicks can race between that check and the call. Losing that race
    // is a duplicate action, not an error, so the room's "wrong phase" exception stops here and never reaches a client.
    private static bool Try<T>(Func<T> action, out T? result)
    {
        try
        {
            result = action();
            return true;
        }
        catch (InvalidOperationException)
        {
            result = default;
            return false;
        }
    }

    private static bool Try(Func<bool> action) => Try(action, out var started) && started;
}
