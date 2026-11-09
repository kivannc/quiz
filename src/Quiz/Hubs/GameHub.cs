using Microsoft.AspNetCore.SignalR;
using Quiz.Game;

namespace Quiz.Hubs;

/// <summary>
/// The wire between the pages and <see cref="Room"/>. It authorizes, looks the room up, calls one method on it
/// and broadcasts the result; no game rule is decided here.
///
/// Groups: every participant is in group <c>code</c>; host screens are also in <c>code + ":host"</c>,
/// which is where <c>AnswerCount</c> and <c>RoomState</c> go. Every host action carries the key and re-joins
/// the caller to both groups, so a reloaded TV page resumes simply by re-sending it. Joining a room is also
/// leaving the one before it: SignalR never takes a connection out of a group on its own, so without that a
/// TV that opened a second room would still be painted by the first, and <see cref="ConnectionRooms"/> is
/// what remembers which groups to leave.
///
/// <see cref="Room"/> is strict about phases and throws when asked for something that makes no sense. A double-clicked
/// button or a reloaded page re-sending an action is normal here, so host actions check the phase first and quietly
/// ignore what does not apply. Failures a page can act on are <see cref="HubException"/>s with a short code.
/// </summary>
public sealed class GameHub(
    RoomStore rooms,
    IHostAuthorizer hosts,
    ConnectionRooms connections,
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
            await JoinPlayerGroup(code);
            var seat = room.Players.First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            await Clients.Caller.SendAsync("Seated", seat.Name, seat.Score);
            await Resync(room);
            await SendRoomState(room);
        }
        return result;
    }

    /// <summary>
    /// Repaints a host screen that has just loaded: joins it to the host groups and sends it, and
    /// only it, whatever is on the TV now. Read-only by design — a screen coming back must never
    /// move the room on, so this reads the same accessors <see cref="Resync"/> does and calls
    /// nothing that changes a room.
    /// </summary>
    public async Task ResumeHost(string code, string hostKey)
    {
        var room = HostRoom(code, hostKey);
        await JoinHostGroups(code);

        // Each accessor takes the room's lock on its own, so a phase that moves on mid-call means a
        // slightly stale paint, which the next broadcast corrects. Nothing here can get it wrong.
        await Clients.Caller.SendAsync("RoomState", room.Players, PhaseName(room.Phase));

        if (room.Phase == RoomPhase.Question && room.CurrentQuestion is { } question)
        {
            // A screen arriving now needs what is left of the limit, not the whole of it: its
            // countdown is what closes the question, and the room goes on refusing answers after
            // the original limit whatever a late screen believes. Truncated, so a resumed screen
            // closes a shade early rather than a shade late, and never below one second, which is
            // reserved to mean "for context only" below.
            var left = Math.Max(1, (int)(room.Remaining ?? TimeSpan.Zero).TotalSeconds);
            await Clients.Caller.SendAsync("QuestionStarted", question with { LimitSeconds = left });
            await Clients.Caller.SendAsync("AnswerCount", room.AnswerCount, room.Players.Count);
            // Only when it is: a screen starts out running, and what it was sent above is the time that
            // is left, which a paused room has stopped spending. Without this it would spend it.
            if (room.Paused) await Clients.Caller.SendAsync("Paused", true);
        }
        else if (room.CurrentReveal is { } reveal)
        {
            // The reveal names the correct marker but not the question it answers, and a screen that
            // has just loaded has never seen one. Send it first, with no time on it, so the screen
            // can say which question this is and read the winning option out; limitSeconds 0 means
            // "context, nothing is open", and the reveal that follows is what gets painted.
            if (room.CurrentQuestion is { } asked)
                await Clients.Caller.SendAsync("QuestionStarted", asked with { LimitSeconds = 0 });
            await Clients.Caller.SendAsync("Revealed", reveal);
        }
        else if (room.Phase == RoomPhase.Finished)
        {
            await Clients.Caller.SendAsync("RoundFinished", room.Standings);
        }
    }

    public async Task StartRound(string code, string hostKey)
    {
        var room = HostRoom(code, hostKey);
        await JoinHostGroups(code);
        if (room.Phase != RoomPhase.Lobby || !Try(room.Start)) return;

        await AnnounceQuestion(room);
        await SendRoomState(room);
    }

    /// <summary>
    /// Records an answer and says whether the room took it. False is an ordinary outcome, not an
    /// error: the question may have closed, the limit may have passed while a host tab was asleep,
    /// or this seat may have answered already. The phone needs the truth to show the player.
    /// </summary>
    public async Task<bool> Answer(string code, Marker marker)
    {
        var room = RoomOrThrow(code);
        if (!room.Answer(Context.ConnectionId, marker)) return false;

        await SendAnswerCount(room);
        return true;
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

    /// <summary>
    /// Stops the question's clock for the whole room. The room itself refuses answers while it is
    /// stopped, so the screen and the server agree about the time; out of phase, or already paused,
    /// this does nothing, like every other host action that does not fit.
    /// </summary>
    public async Task PauseRound(string code, string hostKey)
    {
        var room = HostRoom(code, hostKey);
        await JoinHostGroups(code);
        if (!room.Pause()) return;

        await Clients.Group(code).SendAsync("Paused", true);
    }

    /// <summary>Starts it again with the time that was left. Resuming a room that is not paused does nothing.</summary>
    public async Task ResumeRound(string code, string hostKey)
    {
        var room = HostRoom(code, hostKey);
        await JoinHostGroups(code);
        if (!room.Resume()) return;

        await Clients.Group(code).SendAsync("Paused", false);
    }

    /// <summary>
    /// Ends the room: the phones go back to the join screen, the screen back to its form, and the code
    /// is free to be handed out again. Nothing is kept, so a closed room cannot be resumed — which is
    /// why the screen asks before calling this.
    /// </summary>
    public async Task CloseRoom(string code, string hostKey)
    {
        _ = HostRoom(code, hostKey); // authorizes, and says no-such-room if it has already gone
        if (!rooms.Remove(code)) return;

        await Clients.Group(code).SendAsync("RoomClosed");

        // The groups outlive the room they were named after, and this code may be drawn again within
        // the hour. Empty them, and the ledger with them, so nobody is left listening to a dead room.
        foreach (var id in connections.ForgetRoom(code))
        {
            await Groups.RemoveFromGroupAsync(id, code);
            await Groups.RemoveFromGroupAsync(id, HostGroup(code));
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR drops a gone connection's groups itself, so this only clears the mirror of them, which
        // would otherwise keep an entry for every connection the process has ever seen.
        connections.Forget(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
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
        {
            await Clients.Caller.SendAsync("QuestionStarted", question);
            // Its buzzers would otherwise look live while the room is refusing every tap.
            if (room.Paused) await Clients.Caller.SendAsync("Paused", true);
        }
    }

    private Room RoomOrThrow(string code) => rooms.Find(code) ?? throw new HubException("no-such-room");

    private Room HostRoom(string code, string hostKey)
    {
        if (!hosts.IsHost(hostKey)) throw new HubException("forbidden");
        return RoomOrThrow(code);
    }

    private async Task JoinHostGroups(string code)
    {
        // Driving a room is becoming its host screen, so whatever room this connection was the host screen
        // of before, it is not any more: leave that one's groups before joining these. Re-joining the room
        // it is already hosting reports no move and leaves nothing.
        if (connections.Host(Context.ConnectionId, code) is { } left)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HostGroup(left));
            await LeaveRoomGroup(left);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, code);
        await Groups.AddToGroupAsync(Context.ConnectionId, HostGroup(code));
    }

    /// <summary>Seats this connection in the room's group, leaving the one it was playing in first.</summary>
    private async Task JoinPlayerGroup(string code)
    {
        if (connections.Play(Context.ConnectionId, code) is { } left) await LeaveRoomGroup(left);
        await Groups.AddToGroupAsync(Context.ConnectionId, code);
    }

    // The room's own group carries the question and the reveal, and a connection can be in it as a player
    // or as the host screen. Only leave it once neither of those points at that room any more.
    private async Task LeaveRoomGroup(string code)
    {
        var id = Context.ConnectionId;
        if (connections.Playing(id) == code || connections.Hosting(id) == code) return;
        await Groups.RemoveFromGroupAsync(id, code);
    }

    private async Task AnnounceQuestion(Room room)
    {
        await Clients.Group(room.Code).SendAsync("QuestionStarted", room.CurrentQuestion);
        await SendAnswerCount(room);
    }

    private Task SendAnswerCount(Room room) =>
        Clients.Group(HostGroup(room.Code)).SendAsync("AnswerCount", room.AnswerCount, room.Players.Count);

    private Task SendRoomState(Room room) =>
        Clients.Group(HostGroup(room.Code)).SendAsync("RoomState", room.Players, PhaseName(room.Phase));

    // Phases travel as lowercase words ("lobby", "question"), like every other enum on this wire.
    private static string PhaseName(RoomPhase phase) => phase.ToString().ToLowerInvariant();

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
