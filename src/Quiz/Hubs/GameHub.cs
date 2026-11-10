using Microsoft.AspNetCore.SignalR;
using Quiz.Game;
using Quiz.Pairing;

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
///
/// Screens and consoles: the television shows nothing but a pairing code (<see cref="OpenScreen"/>) and
/// the host's phone, the console, claims it (<see cref="ClaimScreen"/>). The console keeps that claim
/// until it opens a room, and <see cref="CreateRoom"/> then seats both connections in the room's host
/// groups, so every host-bound message reaches the screen and the console alike.
/// </summary>
public sealed class GameHub(
    RoomStore rooms,
    IHostAuthorizer hosts,
    ConnectionRooms connections,
    JoinThrottle joinThrottle,
    [FromKeyedServices(GameHub.CreateThrottleKey)] JoinThrottle createThrottle,
    PairingStore pairings,
    PairingOptions pairingOptions,
    [FromKeyedServices(GameHub.ClaimThrottleKey)] JoinThrottle claimThrottle) : Hub
{
    public const string CreateThrottleKey = "create-room";
    public const string ClaimThrottleKey = "claim-screen";

    private const int MaxNameLength = 24;

    private static string HostGroup(string code) => code + ":host";

    /// <summary>
    /// Gives a television a four-digit pairing code to show, and how long it will last. The only failure
    /// is "pairing-unavailable", which covers both a throttled caller and a full code space: the screen
    /// cannot act on the difference, so it is not told one.
    ///
    /// The lifetime travels with the code because the screen has to replace it before it dies, and
    /// <see cref="PairingOptions.Lifetime"/> is a setting: a screen that assumed the default would show
    /// a code that was already dead on any deployment that shortened it, and would never say so.
    /// </summary>
    public Task<ScreenPairing> OpenScreen() =>
        Task.FromResult(pairings.Issue(Context.ConnectionId, ClientKey()) is { } code
            ? new ScreenPairing(code, (int)pairingOptions.Lifetime.TotalSeconds)
            : throw new HubException("pairing-unavailable"));

    /// <summary>
    /// Makes the caller the console of the screen showing <paramref name="code"/>. Throws "throttled",
    /// "forbidden" or "no-such-code". The key is checked before the code is looked up, and a wrong key
    /// leaves the code untouched, so neither can be used to learn which codes are live — and the attempts
    /// are counted per client, because a hit does not merely reveal a code, it takes the television.
    /// </summary>
    public async Task ClaimScreen(string code, string hostKey)
    {
        if (!claimThrottle.Allow(ClientKey())) throw new HubException("throttled");
        if (!hosts.IsHost(hostKey)) throw new HubException("forbidden");
        if (pairings.Claim(code) is not ClaimResult.Claimed claimed) throw new HubException("no-such-code");

        var screen = claimed.ScreenConnectionId;
        // A connection that claims its own code would satisfy CreateRoom's "has a screen" rule with no
        // television at all. Refused like any other code that cannot be used.
        if (screen == Context.ConnectionId) throw new HubException("no-such-code");

        // False means the screen disconnected after its code was claimed: the code is spent and nothing
        // is left to drive, which is what "no such code" already says.
        if (!connections.Pair(Context.ConnectionId, screen)) throw new HubException("no-such-code");
        await Clients.Client(screen).SendAsync("Paired");

        // A console that is already running a room is re-seating a television that reloaded: it must
        // see that room at once, without a key and without anything else being asked of the host. The
        // claim above stays, so the same console can still open a new room on this screen afterwards.
        if (connections.Hosting(Context.ConnectionId) is { } hosted && rooms.Find(hosted) is { } room)
        {
            await SeatHost(screen, hosted);
            await Clients.Client(screen).SendAsync("RoomOpened", hosted);
            await Repaint(room, Clients.Client(screen));
        }
    }

    /// <summary>
    /// Opens a room and returns its code. Throws "throttled", "forbidden", "no-screen", "unknown-set" or
    /// "too-many-rooms". A room is only ever opened on a claimed screen, because the question and its
    /// options are shown nowhere else.
    /// </summary>
    public async Task<string> CreateRoom(string hostKey, string setId)
    {
        // Counted before the key is checked, so guessing the key is what gets limited.
        if (!createThrottle.Allow(ClientKey())) throw new HubException("throttled");
        if (!hosts.IsHost(hostKey)) throw new HubException("forbidden");
        var screen = connections.Screen(Context.ConnectionId) ?? throw new HubException("no-screen");

        var code = rooms.Create(setId) switch
        {
            CreateResult.Created created => created.Code,
            CreateResult.TooManyRooms => throw new HubException("too-many-rooms"),
            _ => throw new HubException("unknown-set"),
        };

        // The claim is spent only now, so a refused set or a full server leaves it for the retry.
        connections.Unpair(Context.ConnectionId);
        await JoinHostGroups(code);
        await SeatHost(screen, code);
        // The screen has only ever been told "Paired", and a room code is not in any host-bound message.
        await Clients.Client(screen).SendAsync("RoomOpened", code);
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
        await Repaint(room, Clients.Caller);
    }

    // Paints whatever is on a host screen now onto one connection, and only that one.
    private async Task Repaint(Room room, ISingleClientProxy target)
    {
        await target.SendAsync("RoomState", room.Players, PhaseName(room.Phase));

        if (room.Phase == RoomPhase.Question && room.CurrentQuestion is { } question)
        {
            // A screen arriving now needs what is left of the limit, not the whole of it: its
            // countdown is what closes the question, and the room goes on refusing answers after
            // the original limit whatever a late screen believes. Truncated, so a resumed screen
            // closes a shade early rather than a shade late, and never below one second, which is
            // reserved to mean "for context only" below.
            var left = Math.Max(1, (int)(room.Remaining ?? TimeSpan.Zero).TotalSeconds);
            await target.SendAsync("QuestionStarted", question with { LimitSeconds = left });
            await target.SendAsync("AnswerCount", room.AnswerCount, room.Players.Count);
            // Only when it is: a screen starts out running, and what it was sent above is the time that
            // is left, which a paused room has stopped spending. Without this it would spend it.
            if (room.Paused) await target.SendAsync("Paused", true);
        }
        else if (room.CurrentReveal is { } reveal)
        {
            // The reveal names the correct marker but not the question it answers, and a screen that
            // has just loaded has never seen one. Send it first, with no time on it, so the screen
            // can say which question this is and read the winning option out; limitSeconds 0 means
            // "context, nothing is open", and the reveal that follows is what gets painted.
            if (room.CurrentQuestion is { } asked)
                await target.SendAsync("QuestionStarted", asked with { LimitSeconds = 0 });
            await target.SendAsync("Revealed", reveal);
        }
        else if (room.Phase == RoomPhase.Finished)
        {
            await target.SendAsync("RoundFinished", room.Standings);
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

        await Ended(Clients, Groups, connections, code);
    }

    /// <summary>
    /// What the end of a room looks like on the wire, wherever it is ended from — the host closing it, or
    /// the sweeper reclaiming one nobody is sitting at. Everyone in it is told, and then the groups named
    /// after the code are emptied, because the groups outlive the room and the code may be drawn again
    /// within the hour: without this, those phones would be sent the next room's questions while seated
    /// in nothing. Call it only once the room is out of the store.
    ///
    /// Public because its other caller is <c>IdleRoomSweeper</c>, which runs on a timer where nothing
    /// else can reach it: the two ways a room can end have to stay the same way, and this is what the
    /// tests hold them to.
    /// </summary>
    // IHubClients<IClientProxy> is the shape both a hub's own Clients and an IHubContext's have in common.
    public static async Task Ended(IHubClients<IClientProxy> clients, IGroupManager groups, ConnectionRooms connections, string code)
    {
        await clients.Group(code).SendAsync("RoomClosed");

        foreach (var id in connections.ForgetRoom(code))
        {
            await groups.RemoveFromGroupAsync(id, code);
            await groups.RemoveFromGroupAsync(id, HostGroup(code));
        }
    }

    public override Task OnConnectedAsync()
    {
        connections.Connected(Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR drops a gone connection's groups itself, so this only clears the mirror of them, which
        // would otherwise keep an entry for every connection the process has ever seen.
        // A code nobody claimed would otherwise be claimable for a connection that no longer exists. First,
        // so that a connection no longer reading as connected has certainly lost its code too.
        pairings.Forget(Context.ConnectionId);
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

    private Task JoinHostGroups(string code) => SeatHost(Context.ConnectionId, code);

    /// <summary>Makes <paramref name="connectionId"/> the host screen of the room, leaving the one it hosted before.</summary>
    private async Task SeatHost(string connectionId, string code)
    {
        // Driving a room is becoming its host screen, so whatever room this connection was the host screen
        // of before, it is not any more: leave that one's groups before joining these. Re-joining the room
        // it is already hosting reports no move and leaves nothing.
        if (connections.Host(connectionId, code) is { } left)
        {
            await Groups.RemoveFromGroupAsync(connectionId, HostGroup(left));
            await LeaveRoomGroup(connectionId, left);
        }

        await Groups.AddToGroupAsync(connectionId, code);
        await Groups.AddToGroupAsync(connectionId, HostGroup(code));
    }

    /// <summary>Seats this connection in the room's group, leaving the one it was playing in first.</summary>
    private async Task JoinPlayerGroup(string code)
    {
        if (connections.Play(Context.ConnectionId, code) is { } left) await LeaveRoomGroup(Context.ConnectionId, left);
        await Groups.AddToGroupAsync(Context.ConnectionId, code);
    }

    // The room's own group carries the question and the reveal, and a connection can be in it as a player
    // or as the host screen. Only leave it once neither of those points at that room any more.
    private async Task LeaveRoomGroup(string id, string code)
    {
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

/// <summary>
/// What a television is given to show: the code itself and the seconds it has left to live, so the
/// page can ask for the next one in time rather than guessing the server's setting.
/// </summary>
public sealed record ScreenPairing(string Code, int LifetimeSeconds);
