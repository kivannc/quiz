using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Quiz.Hubs;
using Quiz.Pairing;

namespace Quiz.Tests;

/// <summary>A television shows a code, a phone claims it and opens a room, and both see that room.</summary>
public sealed class PairingHubTests : IDisposable
{
    private const string HostKey = "test-host-key-0123456789";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly List<HubConnection> _connections = [];
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly Dictionary<HubConnection, WebApplicationFactory<Program>> _factoryOf = [];

    public void Dispose()
    {
        foreach (var c in _connections) c.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var f in _factories) f.Dispose();
    }

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        var all = new Dictionary<string, string> { ["QUIZ_HOST_KEY"] = HostKey };
        foreach (var (k, v) in settings) all[k] = v;

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in all) b.UseSetting(k, v);
        });
        _factories.Add(factory);
        return factory;
    }

    private async Task<HubConnection> Connect(WebApplicationFactory<Program> factory, string? clientAddress = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hub"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                if (clientAddress is not null) o.Headers["X-Forwarded-For"] = clientAddress;
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))
            .Build();
        _connections.Add(connection);
        _factoryOf[connection] = factory;
        await connection.StartAsync();
        return connection;
    }

    private sealed class Inbox<T>
    {
        private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();
        private readonly ConcurrentQueue<T> _all = new();

        public Inbox(Action<Action<T>> subscribe) => subscribe(Record);

        public Inbox(HubConnection connection, string method) : this(record => connection.On<T>(method, record))
        {
        }

        private void Record(T message)
        {
            _all.Enqueue(message);
            _channel.Writer.TryWrite(message);
        }

        public int Total => _all.Count;

        public async Task<T> Next()
        {
            using var cts = new CancellationTokenSource(Patience);
            return await _channel.Reader.ReadAsync(cts.Token);
        }
    }

    // A message with no arguments: counted, not carried.
    private static Inbox<bool> Signal(HubConnection c, string method) => new(record => c.On(method, () => record(true)));

    private static Inbox<(JsonElement Players, string Phase)> RoomState(HubConnection c) =>
        new(record => c.On<JsonElement, string>("RoomState", (players, phase) => record((players, phase))));

    // Everything the server sent this connection earlier has arrived and been handled by the time this returns.
    private Task Barrier(HubConnection connection) =>
        Settled.After(connection, _factoryOf[connection].Services, Patience);

    private static async Task<HubException> Refused(Func<Task> call) => await Assert.ThrowsAsync<HubException>(call);

    private static Task Claim(HubConnection console, string code, string key = HostKey) =>
        console.InvokeAsync("ClaimScreen", code, key);

    private static Task<string> Create(HubConnection console) =>
        console.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur");

    // The code alone, for the tests that only need something to claim.
    private static async Task<string> Open(HubConnection screen) =>
        (await screen.InvokeAsync<ScreenPairing>("OpenScreen")).Code;

    [Fact]
    public async Task A_screen_gets_a_code_and_a_console_with_the_key_can_claim_it()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var paired = Signal(screen, "Paired");
        var console = await Connect(factory);

        var pairing = await screen.InvokeAsync<ScreenPairing>("OpenScreen");
        Assert.Matches("^[1-9][0-9]{3}$", pairing.Code);
        // The lifetime travels with it: the screen replaces the code before it dies, and the only
        // thing that knows how long that is, is the server.
        Assert.Equal(120, pairing.LifetimeSeconds);
        var code = pairing.Code;

        await Claim(console, code);
        await paired.Next();
        Assert.Equal(1, paired.Total);
    }

    [Fact]
    public async Task Claiming_with_a_wrong_key_is_forbidden_and_the_code_still_works_afterwards()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var paired = Signal(screen, "Paired");
        var code = await Open(screen);

        var stranger = await Connect(factory);
        Assert.Contains("forbidden", (await Refused(() => Claim(stranger, code, "not-the-key-at-all-0000"))).Message);
        // The same refusal for a live code and a dead one: the key is checked before the code is looked at.
        Assert.Contains("forbidden", (await Refused(() => Claim(stranger, "1000", "not-the-key-at-all-0000"))).Message);
        await Barrier(screen);
        Assert.Equal(0, paired.Total);

        var console = await Connect(factory);
        await Claim(console, code);
        await paired.Next();
    }

    [Fact]
    public async Task Claiming_an_unknown_code_says_so_and_claims_are_throttled_per_client()
    {
        var factory = Factory(("QUIZ_CLAIM_ATTEMPTS_PER_MINUTE", "3"));
        var console = await Connect(factory, "203.0.113.7");

        for (var i = 0; i < 3; i++)
            Assert.Contains("no-such-code", (await Refused(() => Claim(console, "1000"))).Message);
        Assert.Contains("throttled", (await Refused(() => Claim(console, "1000"))).Message);

        // A fresh connection from the same address gets no new allowance, and a different address has its own.
        var reconnected = await Connect(factory, "203.0.113.7");
        Assert.Contains("throttled", (await Refused(() => Claim(reconnected, "1000"))).Message);
        var other = await Connect(factory, "203.0.113.8");
        Assert.Contains("no-such-code", (await Refused(() => Claim(other, "1000"))).Message);
    }

    [Fact]
    public async Task The_claim_throttle_counts_a_wrong_key_and_answers_before_the_key_is_checked()
    {
        var factory = Factory(("QUIZ_CLAIM_ATTEMPTS_PER_MINUTE", "2"));
        var attacker = await Connect(factory, "203.0.113.9");

        for (var i = 0; i < 2; i++)
            Assert.Contains("forbidden", (await Refused(() => Claim(attacker, "1000", "guess-number-" + i + "-0000"))).Message);

        // Out of allowance: even another wrong guess is answered "throttled", not "forbidden".
        Assert.Contains("throttled", (await Refused(() => Claim(attacker, "1000", "guess-number-2-0000"))).Message);
    }

    [Fact]
    public async Task A_connection_cannot_claim_its_own_code_to_satisfy_the_no_screen_rule()
    {
        var factory = Factory();
        var both = await Connect(factory);

        var code = await Open(both);
        Assert.Contains("no-such-code", (await Refused(() => Claim(both, code))).Message);

        Assert.Contains("no-screen", (await Refused(() => Create(both))).Message);
    }

    [Fact]
    public async Task A_screen_is_told_the_lifetime_the_server_was_configured_with()
    {
        var factory = Factory(("QUIZ_PAIRING_SECONDS", "45"));
        var screen = await Connect(factory);

        Assert.Equal(45, (await screen.InvokeAsync<ScreenPairing>("OpenScreen")).LifetimeSeconds);
    }

    [Fact]
    public async Task A_screen_that_cannot_get_a_code_is_told_so_without_saying_why()
    {
        var factory = Factory(("QUIZ_PAIRING_ISSUES_PER_MINUTE", "1"));
        var screen = await Connect(factory);
        await Open(screen);

        var ex = await Refused(() => Open(screen));
        Assert.Contains("pairing-unavailable", ex.Message);
        Assert.DoesNotContain("throttle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Codes_are_issued_per_client_so_a_reloaded_screen_buys_no_new_allowance()
    {
        var factory = Factory(("QUIZ_PAIRING_ISSUES_PER_MINUTE", "1"));
        var screen = await Connect(factory, "203.0.113.11");
        await Open(screen);
        Assert.Contains("pairing-unavailable", (await Refused(() => Open(screen))).Message);

        // A fresh connection from the same address gets no new allowance, and a different address has
        // its own: a television that reloads in a loop must not be able to empty the four-digit space.
        var reloaded = await Connect(factory, "203.0.113.11");
        Assert.Contains("pairing-unavailable", (await Refused(() => Open(reloaded))).Message);
        var other = await Connect(factory, "203.0.113.12");
        Assert.Matches("^[1-9][0-9]{3}$", await Open(other));
    }

    [Fact]
    public async Task A_claimed_screen_receives_the_room_the_console_opens()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var opened = new Inbox<string>(screen, "RoomOpened");
        var console = await Connect(factory);
        await Claim(console, await Open(screen));

        var room = await Create(console);

        Assert.Equal(room, await opened.Next());

        // And it is in the room's groups: a player joining is announced to it like to any host screen.
        var screenState = RoomState(screen);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", room, "Ada");
        Assert.Equal("Ada", (await screenState.Next()).Players[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_screen_gets_QuestionStarted_and_Revealed_like_a_host_screen()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var questions = new Inbox<JsonElement>(screen, "QuestionStarted");
        var revealed = new Inbox<JsonElement>(screen, "Revealed");
        var counts = new Inbox<(int, int)>(r => screen.On<int, int>("AnswerCount", (a, t) => r((a, t))));
        var console = await Connect(factory);
        await Claim(console, await Open(screen));
        var room = await Create(console);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", room, "Ada");

        await console.InvokeAsync("StartRound", room, HostKey);
        await questions.Next();
        await counts.Next();

        await console.InvokeAsync("CloseQuestion", room, HostKey);
        await revealed.Next();
    }

    [Fact]
    public async Task A_console_that_drops_leaves_the_round_and_the_screen_alone()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var questions = new Inbox<JsonElement>(screen, "QuestionStarted");
        var revealed = new Inbox<JsonElement>(screen, "Revealed");
        var console = await Connect(factory);
        await Claim(console, await Open(screen));
        var room = await Create(console);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", room, "Ada");
        await console.InvokeAsync("StartRound", room, HostKey);
        await questions.Next();

        await console.StopAsync();

        // The room is still there and the screen is still in it: a second console drives it from the key alone.
        var another = await Connect(factory);
        await another.InvokeAsync("CloseQuestion", room, HostKey);
        await revealed.Next();
        Assert.Equal(1, questions.Total);
    }

    [Fact]
    public async Task A_console_that_returns_can_drive_the_same_room_again()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var revealed = new Inbox<JsonElement>(screen, "Revealed");
        var console = await Connect(factory);
        await Claim(console, await Open(screen));
        var room = await Create(console);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", room, "Ada");
        await console.InvokeAsync("StartRound", room, HostKey);
        await console.StopAsync();

        var returned = await Connect(factory);
        var state = RoomState(returned);
        await returned.InvokeAsync("ResumeHost", room, HostKey);
        Assert.Equal("question", (await state.Next()).Phase);

        await returned.InvokeAsync("CloseQuestion", room, HostKey);
        await revealed.Next();
    }

    [Fact]
    public async Task A_console_that_claims_again_while_its_room_is_open_seats_the_new_screen_in_it()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var console = await Connect(factory);
        await Claim(console, await Open(screen));
        var room = await Create(console);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", room, "Ada");
        await console.InvokeAsync("StartRound", room, HostKey);

        // The television reloads: a new connection, a new code, claimed by the same console.
        var reloaded = await Connect(factory);
        var opened = new Inbox<string>(reloaded, "RoomOpened");
        var state = RoomState(reloaded);
        var questions = new Inbox<JsonElement>(reloaded, "QuestionStarted");
        await Claim(console, await Open(reloaded));

        Assert.Equal(room, await opened.Next());
        Assert.Equal("question", (await state.Next()).Phase);
        await questions.Next();
    }

    // OnDisconnectedAsync runs on the server after the client has stopped, so wait for its effect rather than assume it.
    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task A_screen_that_disconnects_frees_its_pairing_code()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var code = await Open(screen);
        var id = screen.ConnectionId!;
        var connections = factory.Services.GetRequiredService<ConnectionRooms>();
        var store = factory.Services.GetRequiredService<PairingStore>();

        await screen.StopAsync();

        // The hub forgets the code before it forgets the connection, so once the connection reads as gone
        // the code must already be gone from the store itself. Looked at once, because looking consumes.
        await Eventually(() => !connections.IsConnected(id));
        Assert.IsType<ClaimResult.NoSuchCode>(store.Claim(code));
    }

    [Fact]
    public async Task A_screen_that_disconnects_after_being_claimed_takes_the_claim_with_it()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var console = await Connect(factory);
        await Claim(console, await Open(screen));
        var connections = factory.Services.GetRequiredService<ConnectionRooms>();
        Assert.NotNull(connections.Screen(console.ConnectionId!));

        await screen.StopAsync();

        await Eventually(() => connections.Screen(console.ConnectionId!) is null);
        Assert.Contains("no-screen", (await Refused(() => Create(console))).Message);
    }

    [Fact]
    public async Task A_console_that_disconnects_after_claiming_leaves_no_entry_behind()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var console = await Connect(factory);
        await Claim(console, await Open(screen));
        var connections = factory.Services.GetRequiredService<ConnectionRooms>();
        var id = console.ConnectionId!;

        await console.StopAsync();

        await Eventually(() => connections.Screen(id) is null);
    }

    [Fact]
    public async Task Claiming_an_already_claimed_code_is_refused()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var code = await Open(screen);
        var first = await Connect(factory);
        var second = await Connect(factory);

        await Claim(first, code);

        Assert.Contains("no-such-code", (await Refused(() => Claim(second, code))).Message);
        // And the second console has no screen, so it cannot open a room on the first one's television.
        Assert.Contains("no-screen", (await Refused(() => Create(second))).Message);
    }

    [Fact]
    public async Task A_screen_re_pairing_with_another_console_takes_it_from_the_first()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var first = await Connect(factory);
        var second = await Connect(factory);

        await Claim(first, await Open(screen));
        await Claim(second, await Open(screen));

        // One television, one console: only the last to claim it can open a room on it.
        Assert.Contains("no-screen", (await Refused(() => Create(first))).Message);
        Assert.Matches("^[1-9][0-9]{3}$", await Create(second));
    }

    [Fact]
    public async Task CreateRoom_without_a_claimed_screen_is_refused_with_no_screen()
    {
        var factory = Factory();
        var console = await Connect(factory);

        Assert.Contains("no-screen", (await Refused(() => Create(console))).Message);
    }

    [Fact]
    public async Task CreateRoom_with_a_wrong_key_says_forbidden_whether_or_not_a_screen_is_claimed()
    {
        var factory = Factory();
        var console = await Connect(factory);
        Task<string> WrongKey() => console.InvokeAsync<string>("CreateRoom", "not-the-key-at-all-0000", "genel-kultur");

        Assert.Contains("forbidden", (await Refused(WrongKey)).Message);

        var screen = await Connect(factory);
        await Claim(console, await Open(screen));
        Assert.Contains("forbidden", (await Refused(WrongKey)).Message);
    }

    [Fact]
    public async Task A_refused_set_keeps_the_claim_for_the_retry_and_a_used_claim_is_spent()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var console = await Connect(factory);
        await Claim(console, await Open(screen));

        Assert.Contains("unknown-set", (await Refused(() => console.InvokeAsync<string>("CreateRoom", HostKey, "nope"))).Message);
        await Create(console);

        Assert.Contains("no-screen", (await Refused(() => Create(console))).Message);
    }

    [Theory]
    [InlineData("QUIZ_PAIRING_SECONDS", "0")]
    [InlineData("QUIZ_PAIRING_ISSUES_PER_MINUTE", "0")]
    [InlineData("QUIZ_CLAIM_ATTEMPTS_PER_MINUTE", "0")]
    public void A_bad_pairing_setting_stops_startup(string name, string value)
    {
        var factory = Factory((name, value));

        Assert.ThrowsAny<Exception>(() => factory.Server);
    }

    // ---- a room's display: the television it was opened on, and what happens when it goes ----

    /// <summary>
    /// A television, a console that opened a room on it, and one player seated — where the display
    /// tests start. The round is not started: each of them picks the phase it needs.
    /// </summary>
    private sealed record Table(HubConnection Screen, HubConnection Console, string Room, Inbox<bool> Displayed);

    private async Task<Table> Seated(WebApplicationFactory<Program> factory)
    {
        var screen = await Connect(factory);
        var console = await Connect(factory);
        var displayed = new Inbox<bool>(console, "Displayed");
        await Claim(console, await Open(screen));
        var room = await Create(console);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", room, "Ada");
        return new Table(screen, console, room, displayed);
    }

    private static Quiz.Game.Room RoomOf(WebApplicationFactory<Program> factory, string code) =>
        factory.Services.GetRequiredService<Quiz.Game.RoomStore>().Find(code)!;

    [Fact]
    public async Task A_television_that_drops_mid_question_stops_the_round_and_says_so_to_the_console()
    {
        var factory = Factory();
        var table = await Seated(factory);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", table.Room, "Bora");
        var paused = new Inbox<bool>(player, "Paused");
        await table.Console.InvokeAsync("StartRound", table.Room, HostKey);

        await table.Screen.StopAsync();

        Assert.False(await table.Displayed.Next());
        // Pause is room state, so the room stops counting and the phones lock their buzzers. Without
        // it the console's countdown would reach zero and close a question nobody in the room saw.
        Assert.True(await paused.Next());
        Assert.True(RoomOf(factory, table.Room).Paused);
    }

    [Fact]
    public async Task A_television_that_drops_during_a_reveal_says_so_and_leaves_the_room_as_it_is()
    {
        var factory = Factory();
        var table = await Seated(factory);
        await table.Console.InvokeAsync("StartRound", table.Room, HostKey);
        await table.Console.InvokeAsync("CloseQuestion", table.Room, HostKey);

        await table.Screen.StopAsync();

        Assert.False(await table.Displayed.Next());
        // Nothing is running in a reveal, so there is nothing to pause: not advancing is the console's
        // part of this, and the message above is the only thing that can tell it to.
        var room = RoomOf(factory, table.Room);
        Assert.Equal(Quiz.Game.RoomPhase.Reveal, room.Phase);
        Assert.False(room.Paused);
        // And the room came through it: a host who wants to carry on deliberately still can.
        await table.Console.InvokeAsync("AdvanceQuestion", table.Room, HostKey);
        Assert.Equal(Quiz.Game.RoomPhase.Question, room.Phase);
    }

    [Fact]
    public async Task A_television_put_back_in_the_room_is_announced_and_the_round_stays_paused()
    {
        var factory = Factory();
        var table = await Seated(factory);
        await table.Console.InvokeAsync("StartRound", table.Room, HostKey);
        await table.Screen.StopAsync();
        Assert.False(await table.Displayed.Next());

        var reloaded = await Connect(factory);
        var opened = new Inbox<string>(reloaded, "RoomOpened");
        await Claim(table.Console, await Open(reloaded));

        Assert.Equal(table.Room, await opened.Next());
        Assert.True(await table.Displayed.Next());
        // Resuming is the host's call: they are the one who knows whether the room is looking at the
        // television again, so the round is still stopped until the button is pressed.
        Assert.True(RoomOf(factory, table.Room).Paused);
        await table.Console.InvokeAsync("ResumeRound", table.Room, HostKey);
        Assert.False(RoomOf(factory, table.Room).Paused);
    }

    [Fact]
    public async Task A_console_is_not_told_about_a_television_that_is_not_its_rooms()
    {
        var factory = Factory();
        var mine = await Seated(factory);
        var theirScreen = await Connect(factory);
        var theirConsole = await Connect(factory);
        var theirs = new Inbox<bool>(theirConsole, "Displayed");
        await Claim(theirConsole, await Open(theirScreen));
        await Create(theirConsole);

        await mine.Screen.StopAsync();

        // Once the room that lost its television has been told, whatever was sent has been sent.
        Assert.False(await mine.Displayed.Next());
        await Barrier(theirConsole);
        Assert.Equal(0, theirs.Total);
    }

    [Fact]
    public async Task Closing_a_room_drops_the_claim_on_the_television_it_was_shown_on()
    {
        var factory = Factory();
        var table = await Seated(factory);
        // The television reloaded once and was claimed back into the room, which leaves a claim behind.
        var reloaded = await Connect(factory);
        var closed = Signal(reloaded, "RoomClosed");
        await Claim(table.Console, await Open(reloaded));
        Assert.NotNull(factory.Services.GetRequiredService<ConnectionRooms>().Screen(table.Console.ConnectionId!));

        await table.Console.InvokeAsync("CloseRoom", table.Room, HostKey);
        await closed.Next();

        // That television is offering a fresh code again, and the server now agrees that it has to be
        // typed: "close the room, then type the new code the screen shows" is the truth on both sides.
        Assert.Contains("no-screen", (await Refused(() => Create(table.Console))).Message);
    }

    [Fact]
    public async Task A_console_that_comes_back_is_told_whether_the_room_still_has_a_television()
    {
        var factory = Factory();
        var table = await Seated(factory);
        var connections = factory.Services.GetRequiredService<ConnectionRooms>();

        var returning = await Connect(factory);
        var present = new Inbox<bool>(returning, "Displayed");
        await returning.InvokeAsync("ResumeHost", table.Room, HostKey);
        Assert.True(await present.Next());

        await table.Screen.StopAsync();
        await Eventually(() => connections.DisplayOf(table.Room) is null);

        // A console reloading into a room with no television must not arm its pacing and advance it.
        var again = await Connect(factory);
        var gone = new Inbox<bool>(again, "Displayed");
        await again.InvokeAsync("ResumeHost", table.Room, HostKey);
        Assert.False(await gone.Next());
    }

    // The order the messages of one resume arrive in, by method name: the console arms its pacing
    // from the room it is shown, and "Displayed" is what says whether that pacing may act.
    private static Inbox<string> Sequence(HubConnection c, params string[] methods) =>
        new(record =>
        {
            foreach (var method in methods)
            {
                var name = method;
                c.On<JsonElement?>(name, _ => record(name));
            }
        });

    [Fact]
    public async Task A_resumed_console_hears_about_the_display_before_the_question_it_is_resuming_into()
    {
        var factory = Factory();
        var table = await Seated(factory);
        await table.Console.InvokeAsync("StartRound", table.Room, HostKey);

        var returning = await Connect(factory);
        var order = Sequence(returning, "Displayed", "QuestionStarted");
        await returning.InvokeAsync("ResumeHost", table.Room, HostKey);

        // A console starts out believing the room has a television in it, and QuestionStarted is
        // what arms its countdown: told second, there is a message's worth of room — one 200 ms
        // tick — in which it paces a room it has not yet been told nobody can see.
        Assert.Equal("Displayed", await order.Next());
        Assert.Equal("QuestionStarted", await order.Next());
    }

    [Fact]
    public async Task A_resumed_console_hears_about_the_display_before_the_reveal_it_is_resuming_into()
    {
        var factory = Factory();
        var table = await Seated(factory);
        await table.Console.InvokeAsync("StartRound", table.Room, HostKey);
        await table.Console.InvokeAsync("CloseQuestion", table.Room, HostKey);

        var returning = await Connect(factory);
        var order = Sequence(returning, "Displayed", "Revealed");
        await returning.InvokeAsync("ResumeHost", table.Room, HostKey);

        // Same gap in the other phase: Revealed is what arms the beat that opens the next question.
        Assert.Equal("Displayed", await order.Next());
        Assert.Equal("Revealed", await order.Next());
    }

    [Fact]
    public async Task A_second_television_claimed_into_a_live_room_takes_it_off_the_first()
    {
        var factory = Factory();
        var table = await Seated(factory);
        var taken = new Inbox<string>(table.Screen, "RoomTaken");
        var off = new Inbox<JsonElement>(table.Screen, "Revealed");
        await table.Console.InvokeAsync("StartRound", table.Room, HostKey);

        var second = await Connect(factory);
        var opened = new Inbox<string>(second, "RoomOpened");
        var on = new Inbox<JsonElement>(second, "Revealed");
        await Claim(table.Console, await Open(second));

        Assert.Equal(table.Room, await opened.Next());
        // The first screen is out of the room it was painting, and told so in its own terms: the
        // room has not closed, it has moved to another screen.
        Assert.Equal(table.Room, await taken.Next());

        // And nothing in the room reaches it again, or two televisions in one house would be
        // showing the same question and only one of them would notice losing it.
        await table.Console.InvokeAsync("CloseQuestion", table.Room, HostKey);
        await on.Next();
        await Barrier(table.Screen);
        Assert.Equal(0, off.Total);
    }

    [Fact]
    public void A_rooms_display_is_remembered_and_the_room_it_leaves_is_named_when_it_goes()
    {
        var connections = new ConnectionRooms();
        connections.Connected("screen");
        connections.Display("1618", "screen", out _);

        Assert.Equal("screen", connections.DisplayOf("1618"));
        // What the hub acts on: the room the gone connection was the display of, and nothing for any
        // other connection, however many of them end.
        Assert.Null(connections.Forget("console"));
        Assert.Equal("1618", connections.Forget("screen"));
        Assert.Null(connections.DisplayOf("1618"));
    }

    [Fact]
    public void A_television_shows_one_room_at_a_time()
    {
        var connections = new ConnectionRooms();
        connections.Connected("screen");
        connections.Display("1618", "screen", out _);
        connections.Display("2718", "screen", out _);

        Assert.Null(connections.DisplayOf("1618"));
        Assert.Equal("screen", connections.DisplayOf("2718"));
    }

    [Fact]
    public void A_rooms_display_cannot_be_recorded_for_a_screen_that_has_already_gone()
    {
        var connections = new ConnectionRooms();
        connections.Connected("screen");

        // The race: the screen disconnected while the hub was still working on the call that was
        // about to record it. Recording it anyway would name a connection nothing can ever notice
        // leaving again, and the room would be driven blind for the rest of its life.
        connections.Forget("screen");

        Assert.False(connections.Display("1618", "screen", out var replaced));
        Assert.Null(replaced);
        Assert.Null(connections.DisplayOf("1618"));
    }

    [Fact]
    public void Taking_a_rooms_display_names_the_screen_it_replaced()
    {
        var connections = new ConnectionRooms();
        connections.Connected("first");
        connections.Connected("second");

        Assert.True(connections.Display("1618", "first", out var nothing));
        Assert.Null(nothing); // the room had no television in it

        Assert.True(connections.Display("1618", "second", out var replaced));

        // The first screen is up and still painting the room: taking it out of the room's groups is
        // the hub's part, and this is the only thing that says which connection to take out.
        Assert.Equal("first", replaced);
        Assert.Equal("second", connections.DisplayOf("1618"));
        // Seating the same screen again replaces nothing; it would be taken out of its own room.
        Assert.True(connections.Display("1618", "second", out var itself));
        Assert.Null(itself);
    }

    [Fact]
    public void Forgetting_a_room_drops_the_claim_on_the_television_it_was_shown_on()
    {
        var connections = new ConnectionRooms();
        connections.Connected("screen");
        connections.Host("console", "1618");
        connections.Display("1618", "screen", out _);
        Assert.True(connections.Pair("console", "screen"));

        connections.ForgetRoom("1618");

        Assert.Null(connections.Screen("console"));
        Assert.Null(connections.DisplayOf("1618"));
    }

    [Fact]
    public void A_claim_cannot_be_recorded_for_a_screen_that_has_already_disconnected()
    {
        var connections = new ConnectionRooms();
        connections.Connected("screen");
        connections.Connected("console");

        // The race: the code was claimed, then the screen went away before the claim was recorded.
        connections.Forget("screen");

        Assert.False(connections.Pair("console", "screen"));
        Assert.Null(connections.Screen("console"));
    }

    [Fact]
    public void A_claim_on_a_live_screen_is_recorded_and_goes_with_either_side()
    {
        var connections = new ConnectionRooms();
        connections.Connected("screen");

        Assert.True(connections.Pair("console", "screen"));
        Assert.Equal("screen", connections.Screen("console"));

        connections.Forget("screen");
        Assert.Null(connections.Screen("console"));
        Assert.False(connections.Pair("console", "screen")); // and it stays gone
    }

    [Fact]
    public async Task The_sweeper_frees_pairing_codes_that_expired_unclaimed()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var pairings = new PairingStore(clock, new PairingOptions(TimeSpan.FromMinutes(5), 100));
        var factory = Factory();
        var sweeper = new Quiz.Hosting.IdleRoomSweeper(
            factory.Services.GetRequiredService<Quiz.Game.RoomStore>(),
            factory.Services.GetRequiredService<IHubContext<GameHub>>(),
            factory.Services.GetRequiredService<ConnectionRooms>(),
            pairings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Quiz.Hosting.IdleRoomSweeper>.Instance);
        var early = pairings.Issue("screen-1", "client-1")!;
        await sweeper.SweepOnceAsync();
        // A sweeper that freed codes unconditionally would have taken this one.
        Assert.IsType<ClaimResult.Claimed>(pairings.Claim(early));

        Assert.NotNull(pairings.Issue("screen-2", "client-1"));
        clock.Advance(TimeSpan.FromMinutes(6));
        await sweeper.SweepOnceAsync();

        // Had the sweeper not freed it, this sweep would find it and report one.
        Assert.Equal(0, pairings.SweepExpired());
    }
}
