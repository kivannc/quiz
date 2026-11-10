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

    // A round trip on the same connection: everything the server sent to it earlier has arrived by the time this returns.
    private static async Task Barrier(HubConnection connection) =>
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<string>("Join", "0000", "barrier"));

    private static async Task<HubException> Refused(Func<Task> call) => await Assert.ThrowsAsync<HubException>(call);

    private static Task Claim(HubConnection console, string code, string key = HostKey) =>
        console.InvokeAsync("ClaimScreen", code, key);

    private static Task<string> Create(HubConnection console) =>
        console.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur");

    [Fact]
    public async Task A_screen_gets_a_code_and_a_console_with_the_key_can_claim_it()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var paired = Signal(screen, "Paired");
        var console = await Connect(factory);

        var code = await screen.InvokeAsync<string>("OpenScreen");
        Assert.Matches("^[1-9][0-9]{3}$", code);

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
        var code = await screen.InvokeAsync<string>("OpenScreen");

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
    public async Task A_screen_that_cannot_get_a_code_is_told_so_without_saying_why()
    {
        var factory = Factory(("QUIZ_PAIRING_ISSUES_PER_MINUTE", "1"));
        var screen = await Connect(factory);
        await screen.InvokeAsync<string>("OpenScreen");

        var ex = await Refused(() => screen.InvokeAsync<string>("OpenScreen"));
        Assert.Contains("pairing-unavailable", ex.Message);
        Assert.DoesNotContain("throttle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_claimed_screen_receives_the_room_the_console_opens()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var opened = new Inbox<string>(screen, "RoomOpened");
        var console = await Connect(factory);
        await Claim(console, await screen.InvokeAsync<string>("OpenScreen"));

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
        await Claim(console, await screen.InvokeAsync<string>("OpenScreen"));
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
        await Claim(console, await screen.InvokeAsync<string>("OpenScreen"));
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
        await Claim(console, await screen.InvokeAsync<string>("OpenScreen"));
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
        await Claim(console, await screen.InvokeAsync<string>("OpenScreen"));
        var room = await Create(console);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", room, "Ada");
        await console.InvokeAsync("StartRound", room, HostKey);

        // The television reloads: a new connection, a new code, claimed by the same console.
        var reloaded = await Connect(factory);
        var opened = new Inbox<string>(reloaded, "RoomOpened");
        var state = RoomState(reloaded);
        var questions = new Inbox<JsonElement>(reloaded, "QuestionStarted");
        await Claim(console, await reloaded.InvokeAsync<string>("OpenScreen"));

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
        var code = await screen.InvokeAsync<string>("OpenScreen");

        await screen.StopAsync();
        // Nothing observable says the server has finished with the disconnect, and looking at the code
        // consumes it, so give it a moment and then look once: a code still held would be handed to the
        // console as a connection that is gone.
        await Task.Delay(500);

        var console = await Connect(factory);
        Assert.Contains("no-such-code", (await Refused(() => Claim(console, code))).Message);
    }

    [Fact]
    public async Task A_screen_that_disconnects_after_being_claimed_takes_the_claim_with_it()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var console = await Connect(factory);
        await Claim(console, await screen.InvokeAsync<string>("OpenScreen"));
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
        await Claim(console, await screen.InvokeAsync<string>("OpenScreen"));
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
        var code = await screen.InvokeAsync<string>("OpenScreen");
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

        await Claim(first, await screen.InvokeAsync<string>("OpenScreen"));
        await Claim(second, await screen.InvokeAsync<string>("OpenScreen"));

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

        Assert.Contains("forbidden", (await Refused(() =>
            console.InvokeAsync<string>("CreateRoom", "not-the-key-at-all-0000", "genel-kultur"))).Message);
    }

    [Fact]
    public async Task A_refused_set_keeps_the_claim_for_the_retry_and_a_used_claim_is_spent()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var console = await Connect(factory);
        await Claim(console, await screen.InvokeAsync<string>("OpenScreen"));

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
        Assert.NotNull(pairings.Issue("screen-1", "client-1"));

        await sweeper.SweepOnceAsync(); // not expired yet: nothing to free
        clock.Advance(TimeSpan.FromMinutes(6));
        await sweeper.SweepOnceAsync();

        // Had the sweeper not freed it, this sweep would find it and report one.
        Assert.Equal(0, pairings.SweepExpired());
    }
}
