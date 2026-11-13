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
/// the caller to both groups (the two things a room's own television may say, <see cref="TimeUp"/> and
/// <see cref="RevealRead"/>, carry none: being the screen the room is shown on is the authority), so a reloaded TV page resumes simply by re-sending it. Joining a room is also
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
/// groups, so every host-bound message reaches the screen and the console alike. Past that moment the
/// claim is spent, so the room's own tie to its television is <see cref="ConnectionRooms.Display"/>:
/// without it nothing would notice a screen leaving, and the host — whose phone would go on showing
/// buttons — would never be told that the round has stopped because nothing is pacing it. When one
/// goes, <see cref="DisplayGone"/> stops the round and says "Displayed false" to the host group.
/// A room has one television: claiming a second one into a live room takes it off the first, which
/// leaves the room's groups and goes back to showing a pairing code.
/// </summary>
public sealed class GameHub(
    RoomStore rooms,
    IQuestionSetSource sets,
    IHostAuthorizer hosts,
    ConnectionRooms connections,
    JoinThrottle joinThrottle,
    [FromKeyedServices(GameHub.CreateThrottleKey)] JoinThrottle createThrottle,
    PairingStore pairings,
    PairingOptions pairingOptions,
    [FromKeyedServices(GameHub.ClaimThrottleKey)] JoinThrottle claimThrottle,
    [FromKeyedServices(GameHub.AwaitThrottleKey)] JoinThrottle awaitThrottle) : Hub
{
    public const string CreateThrottleKey = "create-room";
    public const string ClaimThrottleKey = "claim-screen";
    public const string AwaitThrottleKey = "await-room";

    private const int MaxNameLength = 24;

    private static string HostGroup(string code) => code + ":host";

    // The phones standing in front of a television that has no room on it yet. Named after the
    // screen's connection and not after its code: the code on screen is replaced every few minutes,
    // and somebody who scanned the last one is still waiting for the same television.
    private static string WaitingGroup(string screen) => "waiting:" + screen;

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
    /// A phone that has scanned a television with no room on it yet waits there: when a room opens on
    /// that screen the phone is told its code ("RoomReady") and can walk straight in. This is what
    /// lets one QR code on the television serve the whole evening, whoever scans it first — the person
    /// who will open the room and the guests who can only wait for it land on the same page.
    ///
    /// Throws "throttled" or "no-such-code". It needs no key, so it does tell a caller whether a
    /// screen code is live — which <see cref="ClaimScreen"/> is careful not to — and a caller that
    /// hits one is told the code of every room later opened on that screen for as long as both stay
    /// connected. That is the price of one code for everybody, and it buys a stranger nothing a
    /// room code would not: both are four digits, both are guessed at the same counted rate, and a
    /// room code is what this ends in. It takes no television and drives nothing; that still needs
    /// the key. Counted per client, apart from joining: a phone that scans a television does one and
    /// then the other, and a whole household shares one address. The code is not spent.
    /// </summary>
    public async Task AwaitRoom(string screenCode)
    {
        if (!awaitThrottle.Allow(ClientKey())) throw new HubException("throttled");
        if (pairings.ScreenShowing(screenCode) is not { } screen) throw new HubException("no-such-code");
        // One television at a time: a phone that waited somewhere else before is not waiting there
        // any more, and must not be told that screen's rooms, or that it has gone.
        if (!(Context.Items.TryGetValue(WaitingAt, out var before) && Equals(before, screen))) await StopWaiting();
        Context.Items[WaitingAt] = screen;
        await Groups.AddToGroupAsync(Context.ConnectionId, WaitingGroup(screen));
        // Remembered for this connection, for ClaimScreen: the phone may turn out to be the one that
        // opens the room, and by the time it has typed a key the code it scanned has been replaced.
        Context.Items[AwaitedScreen] = screen;
    }

    private const string AwaitedScreen = "awaited-screen";
    // Which screen's waiting group this connection is in. Apart from the item above on purpose:
    // that one is spent by a claim, this one by no longer waiting.
    private const string WaitingAt = "waiting-at";

    /// <summary>The wait is over: this connection is in a room, or has gone to wait elsewhere.</summary>
    private async Task StopWaiting()
    {
        if (!Context.Items.Remove(WaitingAt, out var screen)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, WaitingGroup((string)screen!));
    }

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
        // The code as given, or — for a phone that has been waiting at a screen since it scanned a code
        // that has been replaced in the meantime — whatever that same screen is showing now.
        var claim = pairings.Claim(code);
        if (claim is not ClaimResult.Claimed && Context.Items.TryGetValue(AwaitedScreen, out var awaited))
            claim = pairings.ClaimShownBy((string)awaited!);
        if (claim is not ClaimResult.Claimed claimed) throw new HubException("no-such-code");
        // Spent with the claim it bought. Kept, it would turn every later wrong code from this
        // connection into a claim on that same television — which by then may be showing a code
        // again because the room has moved to another screen, and would be pulled back off it.
        Context.Items.Remove(AwaitedScreen);

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
            // Recorded before anything is painted on it, and only if it is still there: this is the
            // room's display now, so the next time this one goes the room notices again — and a
            // screen that went in the window above must not be recorded as one, because then no
            // screen ever would be and the room would be driven blind for the rest of its life.
            if (connections.Display(hosted, screen, out var replaced))
            {
                await SeatHost(screen, hosted);
                // A second television claimed into a live room replaces the first, which is still in
                // the room's groups and still painting the game. It stops, and shows a code again.
                if (replaced is not null) await Unseat(replaced, hosted);
                await Clients.Client(screen).SendAsync("RoomOpened", hosted);
                await Repaint(room, Clients.Client(screen));
                // Whoever scanned this television while it was showing a code again is let in too.
                // Not the caller, which is the host putting the screen back and is in the room already.
                await Clients.GroupExcept(WaitingGroup(screen), new[] { Context.ConnectionId }).SendAsync("RoomReady", hosted);
            }
            // The console has been saying the room had no television in it since the last one left.
            // Now it hears what the room has — which a refused record leaves exactly as it was, and
            // never a television the room does not have. And nothing else: a round stopped when the
            // display went stays stopped, because the host is the one who knows the room is looking
            // at it again.
            //
            // Last, unlike the one in ResumeHost: nothing above repaints a console, so there is no
            // arming to get ahead of here — and a replaced television is still in the host group
            // until Unseat takes it out, so sending this any earlier would send a screen a message
            // it has no handler for.
            await Consoles(hosted).SendAsync("Displayed", connections.DisplayOf(hosted) is not null);
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
        // Refused up front, before a room is drawn on a code nobody can show it. Which screen the
        // room opens on is a separate question, answered below by spending the claim itself.
        if (connections.Screen(Context.ConnectionId) is null) throw new HubException("no-screen");

        var code = rooms.Create(setId) switch
        {
            CreateResult.Created created => created.Code,
            CreateResult.TooManyRooms => throw new HubException("too-many-rooms"),
            _ => throw new HubException("unknown-set"),
        };

        // The claim is spent only now, so a refused set or a full server leaves it for the retry —
        // and it is what spending it answers, not the look above, that says which screen the room
        // opens on: a television that went in between took its claim with it, and there is then
        // nothing to return. Recording the display is what takes the claim's place: the room is
        // shown on that screen and stays related to it for as long as the room lives, which is the
        // only thing that notices the television leaving.
        await JoinHostGroups(code);
        // Two refusals can land here, and neither can be reached from a test against the real ledger.
        // Display refuses only a screen missing from the live set, and Forget alone empties that, so
        // that one is this console's screen going away between Unpair handing it back and Display
        // reading _live — the two calls below take _screenGate separately, and Forget is where a
        // screen's claim and its liveness are dropped together.
        // Unpair returning null has that same cause, between the Screen look above and here, and one
        // more that needs no disconnect at all: ForgetRoom empties the claim of any console whose
        // claimed screen is the departing room's display, and the re-seat branch above deliberately
        // leaves exactly such a claim standing. So a console whose PREVIOUS room is swept for idleness
        // in this window finds its claim gone with every connection still up.
        // Both refusals are deliberate belt-and-braces, not untested paths somebody forgot, and both
        // end in the same honest "Displayed false" below.
        if (connections.Unpair(Context.ConnectionId) is { } display && connections.Display(code, display, out _))
        {
            await SeatHost(display, code);
            // The screen has only ever been told "Paired", and a room code is not in any host-bound message.
            await Clients.Client(display).SendAsync("RoomOpened", code);
            // The phones that scanned this television before it had a room. Not the caller, which
            // may have been waiting there itself until it decided to open the room: it is handed
            // the code as this call's answer.
            await Clients.GroupExcept(WaitingGroup(display), new[] { Context.ConnectionId }).SendAsync("RoomReady", code);
        }
        else
        {
            // The room is open and has no television in it: the screen went inside the moment it
            // took to open it. The console is where the host is looking, so it has to hear that rather
            // than assume the screen whose code it typed a second ago; the re-pair panel is the way back.
            await Clients.Caller.SendAsync("Displayed", false);
        }
        return code;
    }

    /// <summary>
    /// Answers, and does nothing else. A page asks this every few seconds while it has a part in a
    /// room, because an answer is the only proof a connection is alive: a dead one never says so,
    /// and a page that waited to be told would go on showing a question the room left long ago.
    /// </summary>
    public Task Ping() => Task.CompletedTask;

    /// <summary>
    /// A phone asking whether it is still where it thinks it is: seated in <paramref name="code"/>,
    /// on this very connection. If so it is sent the room as it stands, to that connection only,
    /// and the answer is true. It is asked whenever a phone has reason to doubt — its page was in
    /// the background, the network changed — and doubles as the question "is this connection alive
    /// at all": no answer within a few seconds is the phone's cue to start a new one.
    ///
    /// Nothing is counted against it and nothing is given away: it only ever speaks about the
    /// caller's own seat, so a code that is wrong, or a room the caller is not in, reads as false.
    /// </summary>
    public async Task<bool> StillHere(string code)
    {
        if (connections.Playing(Context.ConnectionId) != code || rooms.Find(code) is not { } room) return false;
        // The room's own word on it, not only the ledger's: a seat taken over by another tab has
        // left this connection in the room's group and out of its seat.
        if (!room.HasSeat(Context.ConnectionId)) return false;
        await Resync(room);
        return true;
    }

    /// <summary>Seats a player. Returns the <see cref="JoinResult"/> (camelCase on the wire); a refused join (RoomFull, RoundInProgress) is a result, not an error.</summary>
    public async Task<JoinResult> Join(string code, string name)
    {
        if (!joinThrottle.Allow(ClientKey())) throw new HubException("throttled");
        var room = RoomOrThrow(code);

        name = name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > MaxNameLength) throw new HubException("invalid-name");

        var result = room.Join(name, Context.ConnectionId, out var displaced);
        if (result is JoinResult.Joined or JoinResult.Rejoined)
        {
            // The seat has moved to this connection. Whatever held it before — another tab on the
            // same phone, most often — still has four buzzers on screen that the room now refuses,
            // and nothing else would ever tell it why.
            foreach (var former in displaced) await Clients.Client(former).SendAsync("SeatTaken");
            // In a room, so no longer in front of a television waiting for one: what that screen
            // does next — another room, or going away — is not this phone's business any more.
            await StopWaiting();

            await JoinPlayerGroup(code);
            var seat = room.Players.First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            await Clients.Caller.SendAsync("Seated", seat.Name, seat.Score);
            await Resync(room);
            await SendRoomState(room);
            // Somebody is sitting in this seat again, so an open question is waiting for one more
            // answer than it was a moment ago — which calls off a beat that had started without them.
            if (room.Phase == RoomPhase.Question) await SendAnswerCount(room);
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

        // Whether the room still has a television in it, which a console that has been away cannot
        // know and has to say: the television is what carries the round, so a room without one is
        // waiting, and the console is where the host is offered the way back. Before the repaint,
        // so the notice is up by the time the room is.
        await Clients.Caller.SendAsync("Displayed", connections.DisplayOf(code) is not null);
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
            await target.SendAsync("AnswerCount", room.AnswerCount, room.Present);
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
        await Close(room);
    }

    public async Task AdvanceQuestion(string code, string hostKey)
    {
        var room = HostRoom(code, hostKey);
        await JoinHostGroups(code);
        await MoveOn(room);
    }

    /// <summary>
    /// The room's television says the question it is showing has run out: its time is up, or everyone
    /// has answered and the beat after the last tap has passed. The television is what paces a round,
    /// because it is the one screen a round cannot be played without — a console is a phone, and a
    /// phone gets locked, or put in a pocket, or used to play — so the clock that closes a question is
    /// the clock the room is looking at.
    ///
    /// It carries no key: a screen is in a room by having been claimed into it, and only the screen the
    /// room is shown on may say this ("forbidden" otherwise). It names the question it means, so a
    /// timer that outlived its question cannot close the next one, and a paused room is left alone:
    /// that one is waiting for its host, not for a clock.
    /// </summary>
    public async Task TimeUp(string code, int round, int index)
    {
        var room = DisplayedRoom(code);
        if (room.Paused || room.Phase != RoomPhase.Question || !Showing(room, round, index)) return;
        await Close(room);
    }

    /// <summary>
    /// The room's television says the reveal it is showing has been up long enough, and the round goes
    /// on: to the next question, or to the standings after the last. Same rules as <see cref="TimeUp"/>:
    /// only the room's own screen, and only for the reveal it names.
    /// </summary>
    public async Task RevealRead(string code, int round, int index)
    {
        var room = DisplayedRoom(code);
        if (room.Phase != RoomPhase.Reveal || !Showing(room, round, index)) return;
        await MoveOn(room);
    }

    private static bool Showing(Room room, int round, int index) =>
        room.CurrentQuestion is { } shown && shown.Round == round && shown.Index == index;

    private async Task Close(Room room)
    {
        if (room.Phase != RoomPhase.Question) return;
        if (!Try(room.CloseQuestion, out var reveal)) return;

        await Clients.Group(room.Code).SendAsync("Revealed", reveal);
        await SendRoomState(room);
    }

    private async Task MoveOn(Room room)
    {
        if (room.Phase != RoomPhase.Reveal) return;
        if (!Try(room.Advance, out var another)) return;

        if (another)
        {
            await AnnounceQuestion(room);
        }
        else
        {
            await Clients.Group(room.Code).SendAsync("RoundFinished", room.Standings);
        }
        await SendRoomState(room);
    }

    /// <summary>
    /// Another round in the same room, from whichever set the host picks. The room goes back to its
    /// lobby with everyone still seated and every score at nothing, so the evening carries on without
    /// a new room, a new code or anybody joining again — and the television, still in the room, shows
    /// the room code once more for whoever arrived late. Throws "unknown-set"; out of phase it does
    /// nothing, like every other host action that does not fit.
    /// </summary>
    public async Task NewRound(string code, string hostKey, string setId)
    {
        var room = HostRoom(code, hostKey);
        await JoinHostGroups(code);
        var set = sets.ById(setId) ?? throw new HubException("unknown-set");
        if (!room.NewRound(set)) return;

        // The phones are told the round they were looking at is gone, with the seats as they stand
        // now: a phone has no other way to learn its score went back to nothing. The host screens
        // are in that group too and are sent the lobby right after, which is the message they act on.
        await Clients.Group(code).SendAsync("RoundReset", room.Players);
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

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR drops a gone connection's groups itself, so this only clears the mirror of them, which
        // would otherwise keep an entry for every connection the process has ever seen.
        // A code nobody claimed would otherwise be claimable for a connection that no longer exists. First,
        // so that a connection no longer reading as connected has certainly lost its code too.
        pairings.Forget(Context.ConnectionId);
        // Read before the ledger forgets it: the room this connection was sitting in, if any.
        var seatedIn = connections.Playing(Context.ConnectionId);
        // The one thing about a gone connection that a room has to act on: it was the television.
        if (connections.Forget(Context.ConnectionId) is { } blinded) await DisplayGone(blinded);
        // And the other: it was a player. The seat is kept, but nobody is in it, so the open question
        // stops waiting for it — otherwise one phone that went home makes every question left in the
        // evening run to its limit. The television is told the new count, which is what lets it close
        // a question the players still in the room have all answered.
        if (seatedIn is not null && rooms.Find(seatedIn) is { } room && room.Leave(Context.ConnectionId)
            && room.Phase == RoomPhase.Question)
        {
            await SendAnswerCount(room);
        }
        // If this was a television, the phones waiting in front of it are waiting for a connection
        // that no longer exists: it comes back as a new one, showing a new code, and a room opened
        // there would never reach them. They are told, and go back to "scan it again". After the
        // room's own bookkeeping, which must not depend on a courtesy to the phones.
        await Clients.Group(WaitingGroup(Context.ConnectionId)).SendAsync("ScreenGone");
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// A room's television has left it — reloaded, or a blink of Wi-Fi, which are the same thing here:
    /// a reconnected screen is a new connection in no groups. Nothing may carry the round on while
    /// nobody in the room can see it, so an open question is paused, which stops the room's own clock
    /// and locks the players' buzzers; in the reveal there is no clock to stop, and nothing advances
    /// because the television was what would have. The console is told either way, since the host
    /// holding it is the one who can put a screen back.
    ///
    /// Coming back is deliberately not the reverse of this: <see cref="ClaimScreen"/> says the display
    /// is back and the host presses resume, because the host is the one who can see the room.
    /// </summary>
    private async Task DisplayGone(string code)
    {
        // Closed or swept already: there is no round to stop and nobody left to tell.
        if (rooms.Find(code) is not { } room) return;

        // Pause is state, not a scheduler, so this is the whole of stopping the round.
        if (room.Pause()) await Clients.Group(code).SendAsync("Paused", true);
        await Consoles(code).SendAsync("Displayed", false);
    }

    /// <summary>
    /// A room's consoles: its host group without the television in it. "Displayed" is a message
    /// about the television and is addressed to the phones that act on it; a screen has no handler
    /// for it, and an unhandled method is a warning in the console of a page nobody is reading.
    /// Only this room's group, either way, so a console hears about its own television and no other.
    /// </summary>
    private IClientProxy Consoles(string code) =>
        connections.DisplayOf(code) is { } screen
            ? Clients.GroupExcept(HostGroup(code), new[] { screen })
            : Clients.Group(HostGroup(code));

    /// <summary>
    /// Takes a television out of a room another one has just been claimed into. The first screen is
    /// still in the room's groups and still painting the game, and only one of the two can be the
    /// room's display — so this one leaves, and is told that the room moved to another screen rather
    /// than that it closed, which would be a lie on a screen three metres away.
    /// </summary>
    private async Task Unseat(string screen, string code)
    {
        // Not this room's host screen any more, in the mirror as well as in the groups.
        connections.Unhost(screen, code);
        await Groups.RemoveFromGroupAsync(screen, HostGroup(code));
        await LeaveRoomGroup(screen, code);
        await Clients.Client(screen).SendAsync("RoomTaken", code);
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
            // What this seat has already answered, if it has: the phone may not know. A new tab has
            // no memory of the tap, and a tap whose acknowledgement was lost with the connection was
            // taken all the same — either way four live buzzers would be an offer the room refuses.
            if (room.AnswerOf(Context.ConnectionId) is { } given) await Clients.Caller.SendAsync("Answered", given);
            // Its buzzers would otherwise look live while the room is refusing every tap.
            if (room.Paused) await Clients.Caller.SendAsync("Paused", true);
        }
        // A seat handed back after the last question would otherwise be painted as a lobby, with a
        // score nobody explained and a promise that the round is about to start.
        else if (room.Phase == RoomPhase.Finished)
            await Clients.Caller.SendAsync("RoundFinished", room.Standings);
        // And a seat handed back in a lobby may belong to a phone that slept through the podium and
        // through the host starting another round: it is still showing where it finished, over a
        // score that has gone back to nothing. Told that the round it remembers is over, it waits
        // like everyone else. To a phone joining for the first time this says nothing it did not know.
        else if (room.Phase == RoomPhase.Lobby)
            await Clients.Caller.SendAsync("RoundReset", room.Players);
    }

    private Room RoomOrThrow(string code) => rooms.Find(code) ?? throw new HubException("no-such-room");

    private Room HostRoom(string code, string hostKey)
    {
        if (!hosts.IsHost(hostKey)) throw new HubException("forbidden");
        return RoomOrThrow(code);
    }

    /// <summary>The room, for the screen it is shown on and for nobody else.</summary>
    private Room DisplayedRoom(string code)
    {
        var room = RoomOrThrow(code);
        if (connections.DisplayOf(code) != Context.ConnectionId) throw new HubException("forbidden");
        return room;
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
        Clients.Group(HostGroup(room.Code)).SendAsync("AnswerCount", room.AnswerCount, room.Present);

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
