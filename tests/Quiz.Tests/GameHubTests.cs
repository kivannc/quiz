using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Quiz.Game;
using Quiz.Hubs;

namespace Quiz.Tests;

public sealed class GameHubTests : IDisposable
{
    private const string HostKey = "test-host-key-0123456789";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // The real sets, read once: CorrectMarkerOf is called several times a run and they do not change.
    private static readonly JsonQuestionSetSource Shipped = new(ShippedQuestions.Directory);

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

    /// <summary>Records every message of one name a connection receives, in order.</summary>
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

    // Messages with several arguments arrive as one tuple.
    private static Inbox<(string Name, int Score)> Seated(HubConnection c) =>
        new(record => c.On<string, int>("Seated", (name, score) => record((name, score))));

    private static Inbox<(JsonElement Players, string Phase)> RoomState(HubConnection c) =>
        new(record => c.On<JsonElement, string>("RoomState", (players, phase) => record((players, phase))));

    private static Inbox<(int Answered, int Total)> AnswerCount(HubConnection c) =>
        new(record => c.On<int, int>("AnswerCount", (answered, total) => record((answered, total))));

    // Everything the server sent this connection earlier has arrived and been handled by the time this returns.
    private Task Barrier(HubConnection connection) =>
        Settled.After(connection, _factoryOf[connection].Services, Patience);

    /// <summary>
    /// Which marker answers the question a screen was sent, looked up in the shipped set by its text.
    /// A room draws its ten questions out of a set that holds far more (see <see cref="Room.QuestionsPerRound"/>),
    /// so a test is told which question it got rather than deciding it in advance. A question whose text
    /// is in no shipped set fails here rather than passing: Assert.Single throws on no match.
    /// </summary>
    private static string CorrectMarkerOf(JsonElement question, string setId = "genel-kultur")
    {
        var text = question.GetProperty("text").GetString();
        var set = Shipped.ById(setId);
        Assert.NotNull(set);
        var asked = Assert.Single(set.Questions, q => q.Text == text);
        return JsonNamingPolicy.CamelCase.ConvertName(asked.Correct.ToString());
    }

    /// <summary>Opens a room from <paramref name="host"/>, the way a console must: with a television it has claimed.</summary>
    private async Task<string> Open(HubConnection host, string setId = "genel-kultur")
    {
        await ClaimAScreen(host);
        return await host.InvokeAsync<string>("CreateRoom", HostKey, setId);
    }

    /// <summary>Connects a television, has it show a code, and has <paramref name="console"/> claim it.</summary>
    private async Task<HubConnection> ClaimAScreen(HubConnection console)
    {
        var screen = await Connect(_factoryOf[console]);
        var pairing = await screen.InvokeAsync<ScreenPairing>("OpenScreen");
        await console.InvokeAsync("ClaimScreen", pairing.Code, HostKey);
        return screen;
    }

    private static async Task<HubException> Refused(Func<Task> call) => await Assert.ThrowsAsync<HubException>(call);

    /// <summary>
    /// Sends to both of a room's groups, named the way <c>GameHub</c> names them: the code itself,
    /// which carries the question and the reveal, and <c>code + ":host"</c>, which carries what only
    /// the screens are sent. Used on either side of a room ending, so that silence afterwards means
    /// the groups were emptied rather than that the test was shouting down a name nobody uses.
    /// </summary>
    private static async Task Probe(IHubContext<GameHub> hub, string code)
    {
        await hub.Clients.Group(code).SendAsync("Probe");
        await hub.Clients.Group(code + ":host").SendAsync("HostProbe");
    }

    [Fact]
    public async Task A_host_can_open_a_room_and_a_player_can_join_and_score()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var hostState = RoomState(host);
        var code = await Open(host);
        Assert.Matches("^[1-9][0-9]{3}$", code);

        var player = await Connect(factory);
        var seated = Seated(player);
        var questions = new Inbox<JsonElement>(player, "QuestionStarted");
        var revealed = new Inbox<JsonElement>(player, "Revealed");

        Assert.Equal("joined", await player.InvokeAsync<string>("Join", code, "Ada"));
        Assert.Equal(("Ada", 0), await seated.Next());

        // The host group heard about the lobby, with the new player in it.
        var state = await hostState.Next();
        Assert.Equal("lobby", state.Phase);
        Assert.Equal("Ada", state.Players[0].GetProperty("name").GetString());

        await host.InvokeAsync("StartRound", code, HostKey);
        var correct = CorrectMarkerOf(await questions.Next());
        await player.InvokeAsync("Answer", code, correct);
        await host.InvokeAsync("CloseQuestion", code, HostKey);

        var reveal = await revealed.Next();
        Assert.Equal(correct, reveal.GetProperty("correct").GetString());
        var standing = reveal.GetProperty("standings")[0];
        Assert.Equal("Ada", standing.GetProperty("name").GetString());
        Assert.True(standing.GetProperty("score").GetInt32() > 0);
    }

    [Fact]
    public async Task CreateRoom_with_a_wrong_key_throws_forbidden()
    {
        var host = await Connect(Factory());
        var ex = await Refused(() => host.InvokeAsync<string>("CreateRoom", "not-the-key-at-all-0000", "genel-kultur"));
        Assert.Contains("forbidden", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short-key")]      // 13 characters
    [InlineData("fifteen-chars-x")]    // 15 characters
    public void The_app_refuses_to_start_without_a_usable_host_key(string? key)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            if (key is not null) b.UseSetting("QUIZ_HOST_KEY", key);
        });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var message = (ex.InnerException ?? ex).Message;
        Assert.Contains("QUIZ_HOST_KEY", message);
        Assert.Contains("16", message);
    }

    [Fact]
    public async Task A_sixteen_character_host_key_is_enough_to_start()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("QUIZ_HOST_KEY", "sixteen-chars-ok"));
        using var client = factory.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Healthz_answers_200()
    {
        var response = await Factory().CreateClient().GetAsync("/healthz");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateRoom_attempts_are_throttled_per_connection_when_the_client_address_is_unknown()
    {
        // TestServer supplies no remote address, so the throttle falls back to the connection id here. With a known address the buckets are per address (see the X-Forwarded-For tests).
        var factory = Factory(("QUIZ_CREATE_ATTEMPTS_PER_MINUTE", "3"));
        var attacker = await Connect(factory);
        for (var i = 0; i < 3; i++)
            Assert.Contains("forbidden", (await Refused(() => attacker.InvokeAsync<string>("CreateRoom", "guess-number-" + i + "-0000", "genel-kultur"))).Message);

        // The right key is refused too once the allowance is spent: a throttle that exempts the correct guess would not be one.
        Assert.Contains("throttled", (await Refused(() => attacker.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur"))).Message);

        // Another connection has its own allowance.
        var other = await Connect(factory);
        Assert.Matches("^[1-9][0-9]{3}$", await Open(other));
    }

    [Fact]
    public async Task Create_attempts_default_to_five_a_minute()
    {
        var attacker = await Connect(Factory());
        for (var i = 0; i < 5; i++)
            Assert.Contains("forbidden", (await Refused(() => attacker.InvokeAsync<string>("CreateRoom", "guess-number-" + i + "-0000", "genel-kultur"))).Message);
        Assert.Contains("throttled", (await Refused(() => attacker.InvokeAsync<string>("CreateRoom", "guess-number-5-0000", "genel-kultur"))).Message);
    }

    [Fact]
    public async Task CreateRoom_reports_an_unknown_set_and_a_full_server()
    {
        var factory = Factory(("QUIZ_MAX_ROOMS", "1"));
        var host = await Connect(factory);
        await ClaimAScreen(host);
        Assert.Contains("unknown-set", (await Refused(() => host.InvokeAsync<string>("CreateRoom", HostKey, "nope"))).Message);
        await host.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur");
        await ClaimAScreen(host);
        Assert.Contains("too-many-rooms", (await Refused(() => host.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur"))).Message);
    }

    [Fact]
    public async Task Join_with_an_unknown_code_throws_no_such_room()
    {
        var player = await Connect(Factory());
        var ex = await Refused(() => player.InvokeAsync<string>("Join", "1234", "Ada"));
        Assert.Contains("no-such-room", ex.Message);
    }

    [Fact]
    public async Task Join_attempts_are_throttled_per_connection_when_the_client_address_is_unknown()
    {
        // Falls back to the connection id because TestServer supplies no remote address.
        var player = await Connect(Factory(("QUIZ_JOIN_ATTEMPTS_PER_MINUTE", "2")));
        for (var i = 0; i < 2; i++)
            Assert.Contains("no-such-room", (await Refused(() => player.InvokeAsync<string>("Join", "1234", "Ada"))).Message);
        Assert.Contains("throttled", (await Refused(() => player.InvokeAsync<string>("Join", "1234", "Ada"))).Message);
    }

    [Fact]
    public async Task A_refused_join_is_reported_as_a_result_and_does_not_seat_the_player()
    {
        var factory = Factory(("QUIZ_MAX_PLAYERS", "1"));
        var host = await Connect(factory);
        var code = await Open(host);
        var first = await Connect(factory);
        Assert.Equal("joined", await first.InvokeAsync<string>("Join", code, "Ada"));

        var second = await Connect(factory);
        var seated = Seated(second);
        Assert.Equal("roomFull", await second.InvokeAsync<string>("Join", code, "Bob"));
        await Barrier(second);
        Assert.Equal(0, seated.Total);

        // Nor did the refused player join the room's group: they hear nothing about the round.
        var questions = new Inbox<JsonElement>(second, "QuestionStarted");
        await host.InvokeAsync("StartRound", code, HostKey);
        await Barrier(second);
        Assert.Equal(0, questions.Total);
    }

    [Fact]
    public async Task A_blank_name_is_refused_as_invalid_not_as_a_server_error()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        Assert.Contains("invalid-name", (await Refused(() => player.InvokeAsync<string>("Join", code, "   "))).Message);
    }

    [Fact]
    public async Task Host_actions_with_a_wrong_key_throw_forbidden_and_change_nothing()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", code, "Ada");
        var questions = new Inbox<JsonElement>(player, "QuestionStarted");

        var intruder = await Connect(factory);
        foreach (var action in new[] { "StartRound", "CloseQuestion", "AdvanceQuestion", "PauseRound", "ResumeRound", "CloseRoom" })
            Assert.Contains("forbidden", (await Refused(() => intruder.InvokeAsync(action, code, "wrong-key-wrong-key"))).Message);

        await Barrier(player);
        Assert.Equal(0, questions.Total);

        // The round still starts normally for the real host: the intruder's calls touched nothing.
        await host.InvokeAsync("StartRound", code, HostKey);
        await questions.Next();
    }

    [Fact]
    public async Task Host_actions_on_an_unknown_room_throw_no_such_room()
    {
        var host = await Connect(Factory());
        foreach (var action in new[] { "StartRound", "CloseQuestion", "AdvanceQuestion", "PauseRound", "ResumeRound", "CloseRoom" })
            Assert.Contains("no-such-room", (await Refused(() => host.InvokeAsync(action, "1234", HostKey))).Message);
        Assert.Contains("no-such-room", (await Refused(() => host.InvokeAsync("Answer", "1234", "hearts"))).Message);
    }

    [Fact]
    public async Task A_second_host_connection_with_the_right_key_can_drive_the_live_round()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        var questions = new Inbox<JsonElement>(player, "QuestionStarted");
        await player.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);
        await questions.Next();

        // The TV page reloaded: a brand-new connection resumes by re-sending the key.
        var reloaded = await Connect(factory);
        var answerCounts = AnswerCount(reloaded);
        var revealed = new Inbox<JsonElement>(player, "Revealed");
        await reloaded.InvokeAsync("CloseQuestion", code, HostKey);
        await revealed.Next();
        await reloaded.InvokeAsync("AdvanceQuestion", code, HostKey);
        Assert.Equal(1, (await questions.Next()).GetProperty("index").GetInt32());

        // And it is now a member of the host group, so it hears the live answer count.
        Assert.Equal((0, 1), await answerCounts.Next()); // the second question opening
        await player.InvokeAsync("Answer", code, "hearts");
        Assert.Equal((1, 1), await answerCounts.Next());
    }

    [Fact]
    public async Task A_paused_room_tells_everyone_and_stops_taking_answers()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var phone = await Connect(factory);
        var paused = new Inbox<bool>(phone, "Paused");
        await phone.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);

        await host.InvokeAsync("PauseRound", code, HostKey);
        Assert.True(await paused.Next());
        Assert.False(await phone.InvokeAsync<bool>("Answer", code, "clubs"));

        await host.InvokeAsync("ResumeRound", code, HostKey);
        Assert.False(await paused.Next());
        Assert.True(await phone.InvokeAsync<bool>("Answer", code, "clubs"));
    }

    [Fact]
    public async Task A_pause_out_of_phase_is_ignored_quietly_and_tells_nobody()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var phone = await Connect(factory);
        var paused = new Inbox<bool>(phone, "Paused");
        await phone.InvokeAsync<string>("Join", code, "Ada");

        // In the lobby there is no clock to stop, so this must not reach the phones: a buzzer locked
        // before the round even starts would be the room lying about itself.
        await host.InvokeAsync("PauseRound", code, HostKey);
        await host.InvokeAsync("ResumeRound", code, HostKey);
        await Barrier(phone);
        Assert.Equal(0, paused.Total);

        // The round still starts normally, and pausing works once there is something to pause.
        await host.InvokeAsync("StartRound", code, HostKey);
        await host.InvokeAsync("PauseRound", code, HostKey);
        Assert.True(await paused.Next());

        // Nor during the reveal, where the question's clock is already over.
        await host.InvokeAsync("ResumeRound", code, HostKey);
        Assert.False(await paused.Next());
        await host.InvokeAsync("CloseQuestion", code, HostKey);
        await host.InvokeAsync("PauseRound", code, HostKey);
        await Barrier(phone);
        Assert.Equal(2, paused.Total); // the true and the false from the open question, and nothing else
    }

    [Fact]
    public async Task ResumeHost_says_the_room_is_paused_and_keeps_the_time_that_is_left()
    {
        var factory = Factory(("QUIZ_QUESTION_SECONDS", "30"));
        var host = await Connect(factory);
        var code = await Open(host);
        var phone = await Connect(factory);
        await phone.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);
        await host.InvokeAsync("PauseRound", code, HostKey);

        // The TV reloads while the room is paused: a new connection, the same key.
        var reloaded = await Connect(factory);
        var questions = new Inbox<JsonElement>(reloaded, "QuestionStarted");
        var paused = new Inbox<bool>(reloaded, "Paused");
        await reloaded.InvokeAsync("ResumeHost", code, HostKey);

        var resumed = await questions.Next();
        Assert.Equal(0, resumed.GetProperty("index").GetInt32());
        var left = resumed.GetProperty("limitSeconds").GetInt32();
        Assert.InRange(left, 25, 30); // what the pause stopped at, give or take the test's own moments
        Assert.True(await paused.Next());

        // And it is not being spent: two seconds of real time later, the room says the same number.
        // This is the whole claim — a clock that was only frozen on the screen would have moved.
        await Task.Delay(TimeSpan.FromSeconds(2));
        await reloaded.InvokeAsync("ResumeHost", code, HostKey);
        Assert.Equal(left, (await questions.Next()).GetProperty("limitSeconds").GetInt32());
        Assert.True(await paused.Next());

        // A phone arriving now is told as well, so its buzzers are not left looking live.
        var latecomer = await Connect(factory);
        var pausedOnThePhone = new Inbox<bool>(latecomer, "Paused");
        await latecomer.InvokeAsync<string>("Join", code, "Ada");
        Assert.True(await pausedOnThePhone.Next());
    }

    [Fact]
    public async Task CloseRoom_tells_the_room_and_frees_the_code()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var phone = await Connect(factory);
        var closedOnThePhone = new Inbox<object?>(record => phone.On("RoomClosed", () => record(null)));
        var closedOnTheScreen = new Inbox<object?>(record => host.On("RoomClosed", () => record(null)));
        await phone.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);

        var hub = factory.Services.GetRequiredService<IHubContext<GameHub>>();
        var roomProbes = new Inbox<object?>(record => phone.On("Probe", () => record(null)));
        var hostProbes = new Inbox<object?>(record => host.On("HostProbe", () => record(null)));
        await Probe(hub, code);
        await roomProbes.Next();
        await hostProbes.Next();

        await host.InvokeAsync("CloseRoom", code, HostKey);

        await closedOnThePhone.Next();
        await closedOnTheScreen.Next();
        Assert.Contains("no-such-room", (await Refused(() => phone.InvokeAsync<string>("Join", code, "Ada"))).Message);
        Assert.Contains("no-such-room", (await Refused(() => host.InvokeAsync("ResumeHost", code, HostKey))).Message);

        // And nothing is left listening to the code, which is free to be drawn again: the ledger has
        // forgotten both connections, and a send to the groups it named reaches nobody.
        var connections = factory.Services.GetRequiredService<ConnectionRooms>();
        Assert.Null(connections.Playing(phone.ConnectionId!));
        Assert.Null(connections.Hosting(host.ConnectionId!));
        await Probe(hub, code);
        await Barrier(phone);
        await Barrier(host);
        Assert.Equal(1, roomProbes.Total);  // the phone is out of the room group...
        Assert.Equal(1, hostProbes.Total);  // ...and the screen is out of the host group
        Assert.Equal(1, closedOnThePhone.Total);
    }

    [Fact]
    public async Task A_room_the_sweeper_reclaims_ends_the_way_a_closed_one_does()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var phone = await Connect(factory);
        var closed = new Inbox<object?>(record => phone.On("RoomClosed", () => record(null)));
        await phone.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);

        // Both groups reach their members while the room is alive, so that the same sends after it
        // ends mean something.
        var roomProbes = new Inbox<object?>(record => phone.On("Probe", () => record(null)));
        var hostProbes = new Inbox<object?>(record => host.On("HostProbe", () => record(null)));
        await Probe(factory.Services.GetRequiredService<IHubContext<GameHub>>(), code);
        await roomProbes.Next();
        await hostProbes.Next();

        // Exactly what IdleRoomSweeper does with a room nobody has touched: take it out of the store,
        // then end it on the wire. A room that simply vanished would leave this phone on a dead
        // question, listening to a code that is free to be drawn again.
        var services = factory.Services;
        var swept = services.GetRequiredService<RoomStore>().Remove(code);
        Assert.True(swept);
        var hub = services.GetRequiredService<IHubContext<GameHub>>();
        await GameHub.Ended(hub.Clients, hub.Groups, services.GetRequiredService<ConnectionRooms>(), code);

        await closed.Next();

        // Nothing is left pointing at that code: the ledger has forgotten both connections, and a
        // send to the groups it named now reaches nobody. Asserting it this way, rather than by
        // opening another room, is deliberate — a new room draws a different code, so it would not
        // notice a connection left behind in the old one's group.
        var connections = services.GetRequiredService<ConnectionRooms>();
        Assert.Null(connections.Playing(phone.ConnectionId!));
        Assert.Null(connections.Hosting(host.ConnectionId!));
        await Probe(hub, code);
        await Barrier(phone);
        await Barrier(host);
        Assert.Equal(1, roomProbes.Total); // the phone is out of the room group...
        Assert.Equal(1, hostProbes.Total); // ...and the screen is out of the host group
        Assert.Equal(1, closed.Total);
    }

    [Fact]
    public async Task A_screen_that_opens_a_second_room_is_no_longer_painted_by_the_first()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var first = await Open(screen);
        var second = await Open(screen);

        var states = RoomState(screen);
        var questions = new Inbox<JsonElement>(screen, "QuestionStarted");

        // The room it left carries on without it: someone joins that room, and another screen starts it.
        var stray = await Connect(factory);
        Assert.Equal("joined", await stray.InvokeAsync<string>("Join", first, "Eski"));
        var otherScreen = await Connect(factory);
        await otherScreen.InvokeAsync("StartRound", first, HostKey);

        await Barrier(screen);
        Assert.Equal(0, states.Total);    // not the host group of the room it left...
        Assert.Equal(0, questions.Total); // ...nor that room's own group

        // The room it opened is the one that reaches it now.
        var player = await Connect(factory);
        Assert.Equal("joined", await player.InvokeAsync<string>("Join", second, "Yeni"));
        var state = await states.Next();
        Assert.Equal("lobby", state.Phase);
        Assert.Equal(1, state.Players.GetArrayLength());
        Assert.Equal("Yeni", state.Players[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_phone_that_joins_a_second_room_is_no_longer_sent_the_first_one_s_question()
    {
        var factory = Factory();
        var firstScreen = await Connect(factory);
        var first = await Open(firstScreen);
        var secondScreen = await Connect(factory);
        var second = await Open(secondScreen);

        var phone = await Connect(factory);
        Assert.Equal("joined", await phone.InvokeAsync<string>("Join", first, "Ada"));
        Assert.Equal("joined", await phone.InvokeAsync<string>("Join", second, "Ada"));

        var questions = new Inbox<JsonElement>(phone, "QuestionStarted");
        await firstScreen.InvokeAsync("StartRound", first, HostKey);
        await Barrier(phone);
        Assert.Equal(0, questions.Total);

        await secondScreen.InvokeAsync("StartRound", second, HostKey);
        Assert.Equal(0, (await questions.Next()).GetProperty("index").GetInt32());
        Assert.Equal(1, questions.Total);
    }

    [Fact]
    public async Task Players_receive_QuestionStarted_but_never_the_correct_marker()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        var raw = new Inbox<JsonElement>(player, "QuestionStarted");
        await player.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);

        var q = await raw.Next();
        Assert.Equal(4, q.GetProperty("options").GetArrayLength());
        Assert.Equal(0, q.GetProperty("index").GetInt32());
        // The exact property set, so a leak under any name (answer, isRight, ...) fails here.
        Assert.Equal(["index", "total", "text", "options", "limitSeconds", "round"], q.EnumerateObject().Select(p => p.Name));
        foreach (var option in q.GetProperty("options").EnumerateArray())
            Assert.Equal(["marker", "text"], option.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["hearts", "diamonds", "clubs", "spades"], q.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("marker").GetString()));
    }

    [Fact]
    public async Task The_host_group_receives_AnswerCount_as_answers_arrive()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var counts = AnswerCount(host);
        var code = await Open(host);

        var a = await Connect(factory);
        var b = await Connect(factory);
        await a.InvokeAsync<string>("Join", code, "Ada");
        await b.InvokeAsync<string>("Join", code, "Bob");
        await host.InvokeAsync("StartRound", code, HostKey);

        Assert.Equal((0, 2), await counts.Next());

        await a.InvokeAsync("Answer", code, "hearts");
        Assert.Equal((1, 2), await counts.Next());

        await b.InvokeAsync("Answer", code, "spades");
        Assert.Equal((2, 2), await counts.Next());

        // A repeat answer is refused by the room, so it is not announced.
        await a.InvokeAsync("Answer", code, "clubs");
        await Barrier(host);
        Assert.Equal(3, counts.Total);
    }

    [Fact]
    public async Task Players_do_not_receive_AnswerCount()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        var counts = AnswerCount(player);
        await player.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);
        await player.InvokeAsync("Answer", code, "hearts");
        await Barrier(player);
        Assert.Equal(0, counts.Total);
    }

    [Fact]
    public async Task Duplicate_or_out_of_phase_host_actions_are_ignored_quietly()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        var questions = new Inbox<JsonElement>(player, "QuestionStarted");
        var reveals = new Inbox<JsonElement>(player, "Revealed");
        await player.InvokeAsync<string>("Join", code, "Ada");

        // Out of phase in the lobby: nothing is open to close, nothing to advance from. Neither may throw.
        await host.InvokeAsync("CloseQuestion", code, HostKey);
        await host.InvokeAsync("AdvanceQuestion", code, HostKey);

        // A double-clicked start opens the first question once.
        await host.InvokeAsync("StartRound", code, HostKey);
        await host.InvokeAsync("StartRound", code, HostKey);
        await Barrier(player);
        Assert.Equal(1, questions.Total);

        // Advancing while a question is still open is ignored.
        await host.InvokeAsync("AdvanceQuestion", code, HostKey);
        await Barrier(player);
        Assert.Equal(1, questions.Total);

        // A double-clicked close reveals once.
        await host.InvokeAsync("CloseQuestion", code, HostKey);
        await host.InvokeAsync("CloseQuestion", code, HostKey);
        await Barrier(player);
        Assert.Equal(1, reveals.Total);

        // A double-clicked next opens one further question, not two.
        await host.InvokeAsync("AdvanceQuestion", code, HostKey);
        await host.InvokeAsync("AdvanceQuestion", code, HostKey);
        await Barrier(player);
        Assert.Equal(2, questions.Total);
    }

    [Fact]
    public async Task A_finished_round_is_announced_once_and_further_host_actions_are_ignored()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        var finished = new Inbox<JsonElement>(player, "RoundFinished");
        await player.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);

        // Walk the round to its end: the set is finite, so this terminates.
        var steps = 0;
        while (finished.Total == 0)
        {
            Assert.True(++steps < 100, "the round never finished");
            await host.InvokeAsync("CloseQuestion", code, HostKey);
            await host.InvokeAsync("AdvanceQuestion", code, HostKey);
            await Barrier(player);
        }

        var standings = await finished.Next();
        Assert.Equal("Ada", standings[0].GetProperty("name").GetString());

        await host.InvokeAsync("AdvanceQuestion", code, HostKey);
        await host.InvokeAsync("CloseQuestion", code, HostKey);
        await host.InvokeAsync("StartRound", code, HostKey);
        await Barrier(player);
        Assert.Equal(1, finished.Total);
    }

    [Fact]
    public async Task Connections_from_one_address_share_a_create_throttle_bucket()
    {
        var factory = Factory(("QUIZ_CREATE_ATTEMPTS_PER_MINUTE", "2"));
        var first = await Connect(factory, "203.0.113.7");
        for (var i = 0; i < 2; i++)
            Assert.Contains("forbidden", (await Refused(() => first.InvokeAsync<string>("CreateRoom", "guess-number-" + i + "-0000", "genel-kultur"))).Message);

        // A fresh connection from the same address gets no new allowance...
        var reconnected = await Connect(factory, "203.0.113.7");
        Assert.Contains("throttled", (await Refused(() => reconnected.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur"))).Message);

        // ...while a different address has its own.
        var other = await Connect(factory, "203.0.113.8");
        Assert.Matches("^[1-9][0-9]{3}$", await Open(other));
    }

    [Fact]
    public async Task Connections_from_one_address_share_a_join_throttle_bucket()
    {
        var factory = Factory(("QUIZ_JOIN_ATTEMPTS_PER_MINUTE", "2"));
        var first = await Connect(factory, "203.0.113.7");
        for (var i = 0; i < 2; i++)
            Assert.Contains("no-such-room", (await Refused(() => first.InvokeAsync<string>("Join", "1234", "Ada"))).Message);
        var second = await Connect(factory, "203.0.113.7");
        Assert.Contains("throttled", (await Refused(() => second.InvokeAsync<string>("Join", "1234", "Ada"))).Message);
    }

    [Fact]
    public async Task A_phone_that_rejoins_mid_question_gets_the_current_question_on_that_connection_only()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var other = await Connect(factory);
        var otherQuestions = new Inbox<JsonElement>(other, "QuestionStarted");
        await other.InvokeAsync<string>("Join", code, "Bob");
        var phone = await Connect(factory);
        await phone.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);
        await Barrier(other);
        Assert.Equal(1, otherQuestions.Total);

        // The phone reloads: a new connection, the same name.
        var reloaded = await Connect(factory);
        var questions = new Inbox<JsonElement>(reloaded, "QuestionStarted");
        Assert.Equal("rejoined", await reloaded.InvokeAsync<string>("Join", code, "Ada"));
        var q = await questions.Next();
        Assert.Equal(0, q.GetProperty("index").GetInt32());
        Assert.Equal(4, q.GetProperty("options").GetArrayLength());

        await Barrier(other);
        Assert.Equal(1, otherQuestions.Total); // nobody else was sent it again
    }

    [Fact]
    public async Task A_phone_that_rejoins_during_the_reveal_gets_the_current_reveal()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var phone = await Connect(factory);
        var asked = new Inbox<JsonElement>(phone, "QuestionStarted");
        await phone.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);
        var correct = CorrectMarkerOf(await asked.Next());
        await phone.InvokeAsync("Answer", code, correct);
        await host.InvokeAsync("CloseQuestion", code, HostKey);

        var reloaded = await Connect(factory);
        var questions = new Inbox<JsonElement>(reloaded, "QuestionStarted");
        var reveals = new Inbox<JsonElement>(reloaded, "Revealed");
        await reloaded.InvokeAsync<string>("Join", code, "Ada");
        var reveal = await reveals.Next();
        Assert.Equal(correct, reveal.GetProperty("correct").GetString());
        Assert.True(reveal.GetProperty("standings")[0].GetProperty("score").GetInt32() > 0);
        await Barrier(reloaded);
        Assert.Equal(0, questions.Total);
    }

    [Fact]
    public async Task A_phone_that_rejoins_after_the_round_gets_the_final_standings_on_that_connection_only()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var phone = await Connect(factory);
        var finished = new Inbox<JsonElement>(phone, "RoundFinished");
        await phone.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);

        var steps = 0;
        while (finished.Total == 0)
        {
            Assert.True(++steps < 100, "the round never finished");
            await host.InvokeAsync("CloseQuestion", code, HostKey);
            await host.InvokeAsync("AdvanceQuestion", code, HostKey);
            await Barrier(phone);
        }

        // The phone reloads on the final standings: without being told, it would paint a lobby.
        var reloaded = await Connect(factory);
        var again = new Inbox<JsonElement>(reloaded, "RoundFinished");
        Assert.Equal("rejoined", await reloaded.InvokeAsync<string>("Join", code, "Ada"));
        var standings = await again.Next();
        Assert.Equal("Ada", standings[0].GetProperty("name").GetString());

        await Barrier(phone);
        Assert.Equal(1, finished.Total); // nobody else was sent it again
    }

    /// <summary>Walks an open round to its end from <paramref name="host"/>, watched from <paramref name="player"/>.</summary>
    private async Task PlayOut(HubConnection host, string code, HubConnection player, Inbox<JsonElement> finished)
    {
        var before = finished.Total;
        var steps = 0;
        while (finished.Total == before)
        {
            Assert.True(++steps < 100, "the round never finished");
            await host.InvokeAsync("CloseQuestion", code, HostKey);
            await host.InvokeAsync("AdvanceQuestion", code, HostKey);
            await Barrier(player);
        }
    }

    [Fact]
    public async Task A_new_round_takes_the_same_room_back_to_its_lobby_and_everyone_plays_again()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var hostState = RoomState(host);
        var code = await Open(host);
        var player = await Connect(factory);
        var questions = new Inbox<JsonElement>(player, "QuestionStarted");
        var finished = new Inbox<JsonElement>(player, "RoundFinished");
        var reset = new Inbox<JsonElement>(player, "RoundReset");
        await player.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);
        var first = await questions.Next();
        Assert.Equal(1, first.GetProperty("round").GetInt32());
        await player.InvokeAsync("Answer", code, CorrectMarkerOf(first));
        await PlayOut(host, code, player, finished);
        Assert.True((await finished.Next())[0].GetProperty("score").GetInt32() > 0);

        await host.InvokeAsync("NewRound", code, HostKey, "bilim-ve-doga");

        // The phone is told the round is over with, and how its seat stands now.
        var seats = await reset.Next();
        Assert.Equal("Ada", seats[0].GetProperty("name").GetString());
        Assert.Equal(0, seats[0].GetProperty("score").GetInt32());

        // The host screens are back in a lobby, with the same player in it and nothing on the board.
        await Barrier(host);
        (JsonElement Players, string Phase) state = default;
        while (hostState.Total > 0 && state.Phase != "lobby") state = await hostState.Next();
        while (state.Players[0].GetProperty("score").GetInt32() != 0) state = await hostState.Next();
        Assert.Equal("lobby", state.Phase);
        Assert.Equal("Ada", state.Players[0].GetProperty("name").GetString());

        // A latecomer gets in, the same phone plays without joining again, and the questions are the new set's.
        var late = await Connect(factory);
        Assert.Equal("joined", await late.InvokeAsync<string>("Join", code, "Bob"));
        await host.InvokeAsync("StartRound", code, HostKey);
        JsonElement second;
        do second = await questions.Next(); while (second.GetProperty("round").GetInt32() != 2);
        Assert.Equal(0, second.GetProperty("index").GetInt32());
        Assert.True(await player.InvokeAsync<bool>("Answer", code, CorrectMarkerOf(second, "bilim-ve-doga")));
    }

    [Fact]
    public async Task A_phone_that_slept_through_the_new_round_is_told_about_it_when_it_comes_back()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var phone = await Connect(factory);
        var finished = new Inbox<JsonElement>(phone, "RoundFinished");
        await phone.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);
        await PlayOut(host, code, phone, finished);

        // The phone locks on the podium and its connection goes; the host starts another round.
        await phone.DisposeAsync();
        await host.InvokeAsync("NewRound", code, HostKey, "genel-kultur");

        var back = await Connect(factory);
        var reset = new Inbox<JsonElement>(back, "RoundReset");
        Assert.Equal("rejoined", await back.InvokeAsync<string>("Join", code, "Ada"));
        var seats = await reset.Next();
        Assert.Equal("Ada", seats[0].GetProperty("name").GetString());
        Assert.Equal(0, seats[0].GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task A_new_round_is_ignored_mid_round_and_refused_for_a_wrong_key_or_an_unknown_set()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        var reset = new Inbox<JsonElement>(player, "RoundReset");
        var finished = new Inbox<JsonElement>(player, "RoundFinished");
        await player.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);
        // Joining a lobby says where the seat stands, in the same words; what is counted here is
        // what the refused requests add to that.
        await Barrier(player);
        var told = reset.Total;

        // A question is open: asking for a new round does nothing, like any host action out of phase.
        await host.InvokeAsync("NewRound", code, HostKey, "genel-kultur");
        await Barrier(player);
        Assert.Equal(told, reset.Total);

        await PlayOut(host, code, player, finished);
        Assert.Contains("forbidden", (await Refused(() => host.InvokeAsync("NewRound", code, "wrong-key-wrong-key", "genel-kultur"))).Message);
        Assert.Contains("unknown-set", (await Refused(() => host.InvokeAsync("NewRound", code, HostKey, "no-such-set"))).Message);
        await Barrier(player);
        Assert.Equal(told, reset.Total);
    }

    [Fact]
    public async Task A_lobby_join_is_not_sent_a_question()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        var questions = new Inbox<JsonElement>(player, "QuestionStarted");
        var reveals = new Inbox<JsonElement>(player, "Revealed");
        await player.InvokeAsync<string>("Join", code, "Ada");
        await Barrier(player);
        Assert.Equal(0, questions.Total + reveals.Total);
    }

    [Theory]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public async Task Names_longer_than_24_characters_are_refused(int length, bool accepted)
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        var name = new string('a', length);
        if (accepted)
            Assert.Equal("joined", await player.InvokeAsync<string>("Join", code, name));
        else
            Assert.Contains("invalid-name", (await Refused(() => player.InvokeAsync<string>("Join", code, name))).Message);
    }

    [Fact]
    public async Task The_reveal_has_a_pinned_shape_with_camelCase_marker_keys_in_the_distribution()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        var revealed = new Inbox<JsonElement>(player, "Revealed");
        await player.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);
        await player.InvokeAsync("Answer", code, "clubs");
        await host.InvokeAsync("CloseQuestion", code, HostKey);

        var reveal = await revealed.Next();
        Assert.Equal(["index", "correct", "distribution", "standings", "round"], reveal.EnumerateObject().Select(p => p.Name));
        // The phone subtracts the total it had when this question opened, so it has to be able to tell
        // that this reveal is about that question and not a later one it never saw open.
        Assert.Equal(0, reveal.GetProperty("index").GetInt32());
        var distribution = reveal.GetProperty("distribution");
        Assert.Equal(["hearts", "diamonds", "clubs", "spades"], distribution.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, distribution.GetProperty("clubs").GetInt32());
        Assert.Equal(0, distribution.GetProperty("hearts").GetInt32());
        Assert.Equal(["name", "score"], reveal.GetProperty("standings")[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task The_television_answers_at_the_short_address_a_remote_can_type()
    {
        // "/tv" is the one address anybody enters on that device, and it is a route rather than a
        // static file, so nothing else would notice if a deployment moved the web root underneath it.
        var response = await Factory().CreateClient().GetAsync("/tv");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("tv.js", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sets_lists_every_shipped_set_without_its_questions()
    {
        var response = await Factory().CreateClient().GetAsync("/sets");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var sets = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(11, sets.GetArrayLength());
        Assert.Equal(
            [
                "atasozleri-ve-deyimler", "bilim-ve-doga", "cografya-ve-dunya", "dil-ve-edebiyat",
                "genel-kultur", "hayvanlar-ve-bitkiler", "icatlar-ve-ulasim", "sanat-muzik-ve-film",
                "spor-ve-oyunlar", "tarih-ve-mitoloji", "yemek-ve-mutfak",
            ],
            sets.EnumerateArray().Select(set => set.GetProperty("id").GetString()).Order(StringComparer.Ordinal));

        var first = sets[0];
        // Exactly the three fields a picker needs: no questions, and so no answers, leave the server.
        Assert.Equal(["id", "title", "language"], first.EnumerateObject().Select(f => f.Name));
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("title").GetString()));
        Assert.Equal("tr", first.GetProperty("language").GetString());
    }

    [Fact]
    public async Task ResumeHost_repaints_a_reloaded_screen_mid_question_and_leaves_the_question_open()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", code, "Ada");
        var other = await Connect(factory);
        await other.InvokeAsync<string>("Join", code, "Bob");
        await host.InvokeAsync("StartRound", code, HostKey);
        await player.InvokeAsync("Answer", code, "clubs");

        // The TV page reloads: a new connection, the same key.
        var reloaded = await Connect(factory);
        var states = RoomState(reloaded);
        var questions = new Inbox<JsonElement>(reloaded, "QuestionStarted");
        var counts = AnswerCount(reloaded);
        var reveals = new Inbox<JsonElement>(reloaded, "Revealed");
        await reloaded.InvokeAsync("ResumeHost", code, HostKey);

        var state = await states.Next();
        Assert.Equal("question", state.Phase);
        Assert.Equal(["Ada", "Bob"], state.Players.EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        var question = await questions.Next();
        Assert.Equal(0, question.GetProperty("index").GetInt32());
        Assert.Equal(4, question.GetProperty("options").GetArrayLength());
        Assert.False(question.TryGetProperty("correct", out _));
        Assert.Equal((1, 2), await counts.Next());
        Assert.Equal(0, reveals.Total); // the question was not closed by the screen coming back

        // And the room really is untouched: the other phone can still answer, and the resumed screen hears it.
        await other.InvokeAsync("Answer", code, "hearts");
        Assert.Equal((2, 2), await counts.Next());
    }

    [Fact]
    public async Task ResumeHost_sends_what_is_left_of_the_question_not_the_whole_limit()
    {
        // A screen that opens somewhere else has nothing of its own to count down from, so what the
        // hub tells it is the only thing standing between the players and time the room will refuse.
        var factory = Factory(("QUIZ_QUESTION_SECONDS", "30"));
        var host = await Connect(factory);
        var opening = new Inbox<JsonElement>(host, "QuestionStarted");
        var code = await Open(host);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);

        // The screen that opened the question is told the whole limit.
        Assert.Equal(30, (await opening.Next()).GetProperty("limitSeconds").GetInt32());

        var elsewhere = await Connect(factory);
        var questions = new Inbox<JsonElement>(elsewhere, "QuestionStarted");
        await elsewhere.InvokeAsync("ResumeHost", code, HostKey);

        var resumed = await questions.Next();
        Assert.Equal(0, resumed.GetProperty("index").GetInt32()); // the same question, otherwise unchanged
        // Never zero: zero is reserved for the question sent as context during a reveal.
        Assert.InRange(resumed.GetProperty("limitSeconds").GetInt32(), 1, 29);
    }

    [Fact]
    public async Task ResumeHost_sends_the_reveal_with_the_question_it_answers_and_advances_nothing()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);
        await player.InvokeAsync("Answer", code, "clubs");
        await host.InvokeAsync("CloseQuestion", code, HostKey);

        var reloaded = await Connect(factory);
        var states = RoomState(reloaded);
        var reveals = new Inbox<JsonElement>(reloaded, "Revealed");
        var questions = new Inbox<JsonElement>(reloaded, "QuestionStarted");
        await reloaded.InvokeAsync("ResumeHost", code, HostKey);

        Assert.Equal("reveal", (await states.Next()).Phase);

        // The reveal names the correct marker but not the question it answers, and a screen that has
        // just loaded has never seen one: without this it could not say which question this was, nor
        // read out what the winning option said. No time on it, because nothing is open.
        var context = await questions.Next();
        Assert.Equal(0, context.GetProperty("index").GetInt32());
        Assert.Equal(10, context.GetProperty("total").GetInt32());
        Assert.Equal(0, context.GetProperty("limitSeconds").GetInt32());
        Assert.Equal(4, context.GetProperty("options").GetArrayLength());
        Assert.False(context.TryGetProperty("correct", out _)); // still no answer on this wire

        var reveal = await reveals.Next();
        Assert.Equal(CorrectMarkerOf(context), reveal.GetProperty("correct").GetString());
        await Barrier(reloaded);
        Assert.Equal(1, questions.Total); // the context, and nothing else

        // Still in the reveal: the resumed screen can advance from it, and that next question is a
        // real one, with its whole limit.
        await reloaded.InvokeAsync("AdvanceQuestion", code, HostKey);
        var opened = await questions.Next();
        Assert.Equal(1, opened.GetProperty("index").GetInt32());
        Assert.True(opened.GetProperty("limitSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task ResumeHost_sends_the_standings_when_the_round_is_over()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", code, "Ada");
        var finishedOnTheHost = new Inbox<JsonElement>(host, "RoundFinished");
        await host.InvokeAsync("StartRound", code, HostKey);

        // Walk the round to its end: the set is finite, so this terminates.
        var steps = 0;
        while (finishedOnTheHost.Total == 0)
        {
            Assert.True(++steps < 100, "the round never finished");
            await host.InvokeAsync("CloseQuestion", code, HostKey);
            await host.InvokeAsync("AdvanceQuestion", code, HostKey);
            await Barrier(host);
        }

        var reloaded = await Connect(factory);
        var states = RoomState(reloaded);
        var finished = new Inbox<JsonElement>(reloaded, "RoundFinished");
        await reloaded.InvokeAsync("ResumeHost", code, HostKey);

        Assert.Equal("finished", (await states.Next()).Phase);
        Assert.Equal("Ada", (await finished.Next())[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ResumeHost_in_the_lobby_sends_the_roster_and_nothing_else()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", code, "Ada");

        var reloaded = await Connect(factory);
        var states = RoomState(reloaded);
        var questions = new Inbox<JsonElement>(reloaded, "QuestionStarted");
        var reveals = new Inbox<JsonElement>(reloaded, "Revealed");
        await reloaded.InvokeAsync("ResumeHost", code, HostKey);

        var state = await states.Next();
        Assert.Equal("lobby", state.Phase);
        Assert.Equal("Ada", state.Players[0].GetProperty("name").GetString());
        await Barrier(reloaded);
        Assert.Equal(0, questions.Total + reveals.Total);

        // The resumed screen is in the host group: it hears the next answer count.
        var counts = AnswerCount(reloaded);
        await reloaded.InvokeAsync("StartRound", code, HostKey);
        Assert.Equal((0, 1), await counts.Next());
    }

    [Fact]
    public async Task Answer_reports_whether_the_room_took_it()
    {
        // The phone shows the player what happened to their tap, so a refusal has to come back as a
        // result rather than as silence: a sleeping host tab can leave a question open past its limit.
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var player = await Connect(factory);
        await player.InvokeAsync<string>("Join", code, "Ada");

        Assert.False(await player.InvokeAsync<bool>("Answer", code, "clubs")); // nothing is open yet

        await host.InvokeAsync("StartRound", code, HostKey);
        Assert.True(await player.InvokeAsync<bool>("Answer", code, "clubs"));
        Assert.False(await player.InvokeAsync<bool>("Answer", code, "hearts")); // one answer per seat

        await host.InvokeAsync("CloseQuestion", code, HostKey);
        Assert.False(await player.InvokeAsync<bool>("Answer", code, "clubs")); // the question is over

        var stranger = await Connect(factory);
        Assert.False(await stranger.InvokeAsync<bool>("Answer", code, "clubs")); // no seat, no answer
    }

    [Fact]
    public async Task ResumeHost_needs_the_key_and_an_existing_room()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var stranger = await Connect(factory);

        Assert.Contains("forbidden", (await Refused(() => stranger.InvokeAsync("ResumeHost", code, "not-the-key-at-all-0000"))).Message);
        Assert.Contains("no-such-room", (await Refused(() => stranger.InvokeAsync("ResumeHost", "0000", HostKey))).Message);
    }

    [Theory]
    [InlineData("QUIZ_CREATE_ATTEMPTS_PER_MINUTE", "0")]
    [InlineData("QUIZ_JOIN_ATTEMPTS_PER_MINUTE", "0")]
    [InlineData("QUIZ_MAX_ROOMS", "0")]
    public void A_nonsensical_limit_stops_the_app_from_starting(string setting, string value)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("QUIZ_HOST_KEY", HostKey);
            b.UseSetting(setting, value);
        });
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    /* ---------- the television paces the round ---------- */

    /// <summary>A room with one player in it and a question open, and the television it is shown on.</summary>
    private async Task<(HubConnection Host, HubConnection Screen, HubConnection Phone, string Code)> RoomWithAQuestionOpen()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var screen = await ClaimAScreen(host);
        var code = await host.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur");
        var phone = await Connect(factory);
        await phone.InvokeAsync<string>("Join", code, "Ada");
        await host.InvokeAsync("StartRound", code, HostKey);
        // The first question has been handled by the phone before anything starts counting: a test
        // that listens from here on must not be handed the message that opened the round.
        await Barrier(phone);
        return (host, screen, phone, code);
    }

    [Fact]
    public async Task The_television_closes_the_question_it_is_showing_and_moves_on_from_its_reveal()
    {
        var (_, screen, phone, code) = await RoomWithAQuestionOpen();
        var questions = new Inbox<JsonElement>(phone, "QuestionStarted");
        var reveals = new Inbox<JsonElement>(phone, "Revealed");

        // No key anywhere in this: the screen is in the room by having been claimed into it.
        await screen.InvokeAsync("TimeUp", code, 1, 0);
        var reveal = await reveals.Next();
        Assert.Equal(0, reveal.GetProperty("index").GetInt32());

        await screen.InvokeAsync("RevealRead", code, 1, 0);
        var next = await questions.Next();
        Assert.Equal(1, next.GetProperty("index").GetInt32());
    }

    [Fact]
    public async Task A_television_timer_that_outlived_its_question_moves_nothing()
    {
        var (_, screen, phone, code) = await RoomWithAQuestionOpen();
        var questions = new Inbox<JsonElement>(phone, "QuestionStarted");
        var reveals = new Inbox<JsonElement>(phone, "Revealed");

        // A reveal that is not up yet cannot be read, and a question from another round is not this one.
        await screen.InvokeAsync("RevealRead", code, 1, 0);
        await screen.InvokeAsync("TimeUp", code, 2, 0);
        await screen.InvokeAsync("TimeUp", code, 1, 1);
        await Barrier(phone);
        Assert.Equal(0, reveals.Total);

        await screen.InvokeAsync("TimeUp", code, 1, 0);
        await reveals.Next();
        await screen.InvokeAsync("RevealRead", code, 1, 0);
        await questions.Next();

        // The second question is open now. Everything the first one's timers could still say is late:
        // none of it may close the question the room has only just been asked, or skip past it.
        await screen.InvokeAsync("TimeUp", code, 1, 0);
        await screen.InvokeAsync("RevealRead", code, 1, 0);
        await screen.InvokeAsync("RevealRead", code, 1, 1);
        await Barrier(phone);
        Assert.Equal(1, reveals.Total);
        Assert.Equal(1, questions.Total);
    }

    [Fact]
    public async Task Only_the_screen_a_room_is_shown_on_may_pace_it()
    {
        var (host, _, phone, code) = await RoomWithAQuestionOpen();
        var reveals = new Inbox<JsonElement>(phone, "Revealed");

        // A player, the console itself, and a television that is switched on but in no room.
        var stranger = await Connect(_factoryOf[host]);
        await stranger.InvokeAsync<ScreenPairing>("OpenScreen");
        foreach (var caller in new[] { phone, host, stranger })
        {
            Assert.Contains("forbidden", (await Refused(() => caller.InvokeAsync("TimeUp", code, 1, 0))).Message);
            Assert.Contains("forbidden", (await Refused(() => caller.InvokeAsync("RevealRead", code, 1, 0))).Message);
        }
        Assert.Contains("no-such-room", (await Refused(() => stranger.InvokeAsync("TimeUp", "0000", 1, 0))).Message);

        await Barrier(phone);
        Assert.Equal(0, reveals.Total);
    }

    [Fact]
    public async Task A_paused_question_is_not_closed_by_the_television()
    {
        var (host, screen, phone, code) = await RoomWithAQuestionOpen();
        var reveals = new Inbox<JsonElement>(phone, "Revealed");

        await host.InvokeAsync("PauseRound", code, HostKey);
        await screen.InvokeAsync("TimeUp", code, 1, 0);
        await Barrier(phone);
        Assert.Equal(0, reveals.Total);

        await host.InvokeAsync("ResumeRound", code, HostKey);
        await screen.InvokeAsync("TimeUp", code, 1, 0);
        await reveals.Next();
    }

    [Fact]
    public async Task The_television_carries_a_whole_round_to_its_standings_with_no_console_in_it()
    {
        var (host, screen, phone, code) = await RoomWithAQuestionOpen();
        var finished = new Inbox<JsonElement>(phone, "RoundFinished");
        var questions = new Inbox<JsonElement>(phone, "QuestionStarted");

        // The console that opened the room is gone for good: locked, in a pocket, or playing.
        await host.DisposeAsync();

        for (var index = 0; index < Room.QuestionsPerRound; index++)
        {
            await screen.InvokeAsync("TimeUp", code, 1, index);
            await screen.InvokeAsync("RevealRead", code, 1, index);
        }

        var standings = await finished.Next();
        Assert.Equal("Ada", standings[0].GetProperty("name").GetString());
        await Barrier(phone);
        Assert.Equal(Room.QuestionsPerRound - 1, questions.Total);
        Assert.Equal(1, finished.Total);
    }

    /* ---------- one code on the television for everybody ---------- */

    [Fact]
    public async Task A_phone_that_scanned_the_television_first_is_told_the_room_when_it_opens()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var shown = await screen.InvokeAsync<ScreenPairing>("OpenScreen");

        // A guest scans before anybody has opened a room. No key, and the code is not spent by it.
        var guest = await Connect(factory);
        var ready = new Inbox<string>(guest, "RoomReady");
        await guest.InvokeAsync("AwaitRoom", shown.Code);

        // The person with the key scans the same code, waits there for a moment too, then opens it.
        var opener = await Connect(factory);
        var told = new Inbox<string>(opener, "RoomReady");
        await opener.InvokeAsync("AwaitRoom", shown.Code);
        await opener.InvokeAsync("ClaimScreen", shown.Code, HostKey);
        var code = await opener.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur");

        Assert.Equal(code, await ready.Next());
        Assert.Equal("joined", await guest.InvokeAsync<string>("Join", code, "Ada"));
        // The opener has the code as its answer and is not told a second time.
        await Barrier(opener);
        Assert.Equal(0, told.Total);
    }

    [Fact]
    public async Task Waiting_at_a_television_needs_a_live_code_and_hears_only_that_television()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var here = await Connect(factory);
        var there = await Connect(factory);
        var codeHere = (await here.InvokeAsync<ScreenPairing>("OpenScreen")).Code;
        var codeThere = (await there.InvokeAsync<ScreenPairing>("OpenScreen")).Code;

        var guest = await Connect(factory);
        var ready = new Inbox<string>(guest, "RoomReady");
        Assert.Contains("no-such-code", (await Refused(() => guest.InvokeAsync("AwaitRoom", "0000"))).Message);
        await guest.InvokeAsync("AwaitRoom", codeHere);

        // A room on the other television is none of this guest's business.
        await host.InvokeAsync("ClaimScreen", codeThere, HostKey);
        await host.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur");
        await Barrier(guest);
        Assert.Equal(0, ready.Total);

        // A code that has been claimed is no longer on any screen.
        Assert.Contains("no-such-code", (await Refused(() => guest.InvokeAsync("AwaitRoom", codeThere))).Message);
    }

    [Fact]
    public async Task Waiting_at_a_television_is_counted_per_client_and_apart_from_joining()
    {
        var factory = Factory(("QUIZ_JOIN_ATTEMPTS_PER_MINUTE", "2"));
        var screen = await Connect(factory);
        var shown = await screen.InvokeAsync<ScreenPairing>("OpenScreen");
        var guest = await Connect(factory);

        await guest.InvokeAsync("AwaitRoom", shown.Code);
        await guest.InvokeAsync("AwaitRoom", shown.Code);
        Assert.Contains("throttled", (await Refused(() => guest.InvokeAsync("AwaitRoom", shown.Code))).Message);

        // Counted apart from joining: a phone that scans a television waits there and then joins, and
        // the waiting must not have eaten the join. Two joins are still its to make.
        for (var i = 0; i < 2; i++)
            Assert.Contains("no-such-room", (await Refused(() => guest.InvokeAsync<string>("Join", "0000", "Ada"))).Message);
        Assert.Contains("throttled", (await Refused(() => guest.InvokeAsync<string>("Join", "0000", "Ada"))).Message);
    }

    [Fact]
    public async Task Phones_waiting_at_a_television_are_told_when_it_goes()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var shown = await screen.InvokeAsync<ScreenPairing>("OpenScreen");
        var guest = await Connect(factory);
        var gone = new Inbox<bool>(record => guest.On("ScreenGone", () => record(true)));
        var elsewhere = await Connect(factory);
        var unrelated = new Inbox<bool>(record => elsewhere.On("ScreenGone", () => record(true)));
        await guest.InvokeAsync("AwaitRoom", shown.Code);

        // A blink of Wi-Fi or a reload: the television comes back as another connection with another
        // code, and a room opened there would never reach a phone waiting for this one.
        await screen.DisposeAsync();

        Assert.True(await gone.Next());
        await Barrier(elsewhere);
        Assert.Equal(0, unrelated.Total);
    }

    [Fact]
    public async Task A_seat_taken_over_by_another_connection_tells_the_one_that_lost_it()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);
        var first = await Connect(factory);
        var lost = new Inbox<bool>(record => first.On("SeatTaken", () => record(true)));
        Assert.Equal("joined", await first.InvokeAsync<string>("Join", code, "Ada"));

        // Sending the name again from the same connection is not losing the seat.
        Assert.Equal("rejoined", await first.InvokeAsync<string>("Join", code, "Ada"));
        await Barrier(first);
        Assert.Equal(0, lost.Total);

        // A second tab under the same name takes it, and the first is told rather than left with
        // buzzers the room refuses.
        var second = await Connect(factory);
        var kept = new Inbox<bool>(record => second.On("SeatTaken", () => record(true)));
        Assert.Equal("rejoined", await second.InvokeAsync<string>("Join", code, "Ada"));
        Assert.True(await lost.Next());

        await host.InvokeAsync("StartRound", code, HostKey);
        Assert.False(await first.InvokeAsync<bool>("Answer", code, "clubs"));
        Assert.True(await second.InvokeAsync<bool>("Answer", code, "clubs"));
        await Barrier(second);
        Assert.Equal(0, kept.Total);
    }

    [Fact]
    public async Task A_wait_is_spent_by_the_claim_it_bought_and_cannot_move_the_room_later()
    {
        var factory = Factory();
        var here = await Connect(factory);
        var scanned = (await here.InvokeAsync<ScreenPairing>("OpenScreen")).Code;
        var phone = await Connect(factory);
        await phone.InvokeAsync("AwaitRoom", scanned);
        await phone.InvokeAsync("ClaimScreen", scanned, HostKey);
        var code = await phone.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur");

        // The room moves to another television, so the first one is showing a code again.
        var there = await Connect(factory);
        var taken = new Inbox<string>(here, "RoomTaken");
        await phone.InvokeAsync("ClaimScreen", (await there.InvokeAsync<ScreenPairing>("OpenScreen")).Code, HostKey);
        Assert.Equal(code, await taken.Next());
        await here.InvokeAsync<ScreenPairing>("OpenScreen");

        // A wrong code from the host now is only a wrong code: it must not fall back to the screen
        // this phone once waited at and pull the room off the television it is on.
        var moved = new Inbox<string>(there, "RoomTaken");
        Assert.Contains("no-such-code", (await Refused(() => phone.InvokeAsync("ClaimScreen", "0000", HostKey))).Message);
        await Barrier(there);
        Assert.Equal(0, moved.Total);
    }

    [Fact]
    public async Task A_phone_that_scans_a_television_that_reloaded_is_told_the_room_once_it_is_back_in_it()
    {
        var factory = Factory();
        var host = await Connect(factory);
        var code = await Open(host);

        // The television reloaded: a new connection, showing a code again, with the round still on.
        var reloaded = await Connect(factory);
        var shown = await reloaded.InvokeAsync<ScreenPairing>("OpenScreen");
        var guest = await Connect(factory);
        var ready = new Inbox<string>(guest, "RoomReady");
        await guest.InvokeAsync("AwaitRoom", shown.Code);

        var told = new Inbox<string>(host, "RoomReady");
        await host.InvokeAsync("ClaimScreen", shown.Code, HostKey);
        Assert.Equal(code, await ready.Next());
        // The host putting the screen back is in the room already and is not told about it.
        await Barrier(host);
        Assert.Equal(0, told.Total);
    }

    [Fact]
    public async Task A_phone_waits_at_one_television_at_a_time_and_not_at_all_once_it_is_in_a_room()
    {
        var factory = Factory(("QUIZ_MAX_ROOMS", "3"));
        var host = await Connect(factory);
        var first = await Connect(factory);
        var second = await Connect(factory);
        var codeFirst = (await first.InvokeAsync<ScreenPairing>("OpenScreen")).Code;
        var codeSecond = (await second.InvokeAsync<ScreenPairing>("OpenScreen")).Code;

        var phone = await Connect(factory);
        var ready = new Inbox<string>(phone, "RoomReady");
        var gone = new Inbox<bool>(record => phone.On("ScreenGone", () => record(true)));
        await phone.InvokeAsync("AwaitRoom", codeFirst);
        await phone.InvokeAsync("AwaitRoom", codeSecond);

        // The first television going is no longer this phone's business: it walked over to the second.
        await first.DisposeAsync();
        await Barrier(phone);
        Assert.Equal(0, gone.Total);

        // The second one's room is, and joining it ends the wait: a later room on that same screen
        // is not announced to a phone that is already playing.
        await host.InvokeAsync("ClaimScreen", codeSecond, HostKey);
        var code = await host.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur");
        Assert.Equal(code, await ready.Next());
        Assert.Equal("joined", await phone.InvokeAsync<string>("Join", code, "Ada"));

        await host.InvokeAsync("CloseRoom", code, HostKey);
        await host.InvokeAsync("ClaimScreen", (await second.InvokeAsync<ScreenPairing>("OpenScreen")).Code, HostKey);
        await host.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur");
        await second.DisposeAsync();
        await Barrier(phone);
        Assert.Equal(1, ready.Total);
        Assert.Equal(0, gone.Total);
    }

    [Fact]
    public async Task The_pages_are_served_so_that_a_browser_asks_before_reusing_one()
    {
        var client = Factory().CreateClient();
        foreach (var path in new[] { "/", "/join.js", "/tv", "/tv.js", "/app.css" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoCache, path);
        }
    }

    [Fact]
    public async Task The_phone_that_opens_a_room_can_sit_in_it_and_drive_it()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var shown = await screen.InvokeAsync<ScreenPairing>("OpenScreen");

        // One connection for all of it: claim the screen, open the room, take a seat.
        var phone = await Connect(factory);
        var questions = new Inbox<JsonElement>(phone, "QuestionStarted");
        var reveals = new Inbox<JsonElement>(phone, "Revealed");
        var states = RoomState(phone);
        await phone.InvokeAsync("ClaimScreen", shown.Code, HostKey);
        var code = await phone.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur");
        Assert.Equal("joined", await phone.InvokeAsync<string>("Join", code, "Ada"));

        await phone.InvokeAsync("StartRound", code, HostKey);
        var question = await questions.Next();
        Assert.True(await phone.InvokeAsync<bool>("Answer", code, CorrectMarkerOf(question)));

        // The television closes it, and the phone that is both host and player is scored like any other.
        await screen.InvokeAsync("TimeUp", code, 1, 0);
        var reveal = await reveals.Next();
        Assert.True(reveal.GetProperty("standings")[0].GetProperty("score").GetInt32() > 0);

        // It is in the room's group twice over, as its host and as a player, and still hears each
        // thing once; and it hears what only a host is told, which is how it knows who has joined.
        await Barrier(phone);
        Assert.Equal(1, questions.Total);
        Assert.Equal(1, reveals.Total);
        Assert.True(states.Total > 0);
    }

    [Fact]
    public async Task A_phone_waiting_at_a_television_can_still_claim_it_after_its_code_was_replaced()
    {
        var factory = Factory();
        var screen = await Connect(factory);
        var scanned = (await screen.InvokeAsync<ScreenPairing>("OpenScreen")).Code;

        var phone = await Connect(factory);
        await phone.InvokeAsync("AwaitRoom", scanned);
        // The television replaces its code while the key is being typed.
        var current = (await screen.InvokeAsync<ScreenPairing>("OpenScreen")).Code;

        // A phone that was never in front of this screen gets nowhere with the old code...
        var stranger = await Connect(factory);
        if (scanned != current)
            Assert.Contains("no-such-code", (await Refused(() => stranger.InvokeAsync("ClaimScreen", scanned, HostKey))).Message);
        // ...and the one that was still needs the key.
        Assert.Contains("forbidden", (await Refused(() => phone.InvokeAsync("ClaimScreen", scanned, "not-the-key"))).Message);

        await phone.InvokeAsync("ClaimScreen", scanned, HostKey);
        var code = await phone.InvokeAsync<string>("CreateRoom", HostKey, "genel-kultur");
        Assert.Matches("^[0-9]{4}$", code);

        // The screen is in a room now and shows no code: there is nothing left there to claim.
        var late = await Connect(factory);
        Assert.Contains("no-such-code", (await Refused(() => late.InvokeAsync("AwaitRoom", current))).Message);
        Assert.Contains("no-such-code", (await Refused(() => phone.InvokeAsync("ClaimScreen", scanned, HostKey))).Message);
    }

    [Fact]
    public async Task The_old_console_address_leads_to_the_page_everybody_uses()
    {
        // Followed by hand: a client that follows redirects would hide where it was sent.
        var client = Factory().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/host.html");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/host.js")).StatusCode);
    }

    /* ---------- a phone that doubts its connection ---------- */

    [Fact]
    public async Task A_phone_that_asks_is_told_whether_it_is_still_in_its_seat_and_what_the_room_is_doing()
    {
        var (host, _, phone, code) = await RoomWithAQuestionOpen();
        var questions = new Inbox<JsonElement>(phone, "QuestionStarted");
        var answered = new Inbox<string>(phone, "Answered");

        // Alive at all: an answer, and nothing else — nothing is sent because of it.
        await phone.InvokeAsync("Ping");
        await Barrier(phone);
        Assert.Equal(0, questions.Total);

        // In its seat: true, and the question again, to this phone only.
        Assert.True(await phone.InvokeAsync<bool>("StillHere", code));
        Assert.Equal(0, (await questions.Next()).GetProperty("index").GetInt32());
        await Barrier(phone);
        Assert.Equal(0, answered.Total);

        // Once it has answered, asking again also says so.
        Assert.True(await phone.InvokeAsync<bool>("Answer", code, "clubs"));
        Assert.True(await phone.InvokeAsync<bool>("StillHere", code));
        Assert.Equal("clubs", await answered.Next());

        // Not for a room it is not in, not for one that does not exist, and not for a caller with no seat.
        Assert.False(await phone.InvokeAsync<bool>("StillHere", "0000"));
        Assert.False(await host.InvokeAsync<bool>("StillHere", code));
        var stranger = await Connect(_factoryOf[host]);
        Assert.False(await stranger.InvokeAsync<bool>("StillHere", code));
    }

    [Fact]
    public async Task A_seat_that_moved_to_another_connection_keeps_its_answer_and_the_old_one_is_no_longer_there()
    {
        var (host, _, phone, code) = await RoomWithAQuestionOpen();
        Assert.True(await phone.InvokeAsync<bool>("Answer", code, "spades"));

        // A new tab: nothing remembered, the same name. It is told the answer is already in.
        var tab = await Connect(_factoryOf[host]);
        var answered = new Inbox<string>(tab, "Answered");
        Assert.Equal("rejoined", await tab.InvokeAsync<string>("Join", code, "Ada"));
        Assert.Equal("spades", await answered.Next());
        Assert.False(await tab.InvokeAsync<bool>("Answer", code, "clubs"));

        // The connection that lost the seat is still in the room's group, and is told the truth.
        Assert.False(await phone.InvokeAsync<bool>("StillHere", code));
        Assert.True(await tab.InvokeAsync<bool>("StillHere", code));
    }
}
