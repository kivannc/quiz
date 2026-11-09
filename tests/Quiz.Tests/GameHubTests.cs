using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Quiz.Tests;

public sealed class GameHubTests : IDisposable
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

    private async Task<HubConnection> Connect(WebApplicationFactory<Program> factory)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hub"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))
            .Build();
        _connections.Add(connection);
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

    // A round trip on the same connection: everything the server sent to it earlier has arrived by the time this returns.
    private static async Task Barrier(HubConnection connection) =>
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<string>("Join", "0000", "barrier"));

    private static async Task<string> Open(HubConnection host, string setId = "genel-kultur") =>
        await host.InvokeAsync<string>("CreateRoom", HostKey, setId);

    private static async Task<HubException> Refused(Func<Task> call) => await Assert.ThrowsAsync<HubException>(call);

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

        Assert.Equal("Joined", await player.InvokeAsync<string>("Join", code, "Ada"));
        Assert.Equal(("Ada", 0), await seated.Next());

        // The host group heard about the lobby, with the new player in it.
        var state = await hostState.Next();
        Assert.Equal("lobby", state.Phase);
        Assert.Equal("Ada", state.Players[0].GetProperty("name").GetString());

        await host.InvokeAsync("StartRound", code, HostKey);
        await questions.Next();
        await player.InvokeAsync("Answer", code, "clubs"); // gk-01: the capital of Turkey is Ankara (clubs)
        await host.InvokeAsync("CloseQuestion", code, HostKey);

        var reveal = await revealed.Next();
        Assert.Equal("clubs", reveal.GetProperty("correct").GetString());
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
    public void A_sixteen_character_host_key_is_enough_to_start()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("QUIZ_HOST_KEY", "sixteen-chars-ok"));
        using var client = factory.CreateClient();
    }

    [Fact]
    public async Task Healthz_answers_200()
    {
        var response = await Factory().CreateClient().GetAsync("/healthz");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateRoom_attempts_are_throttled_per_connection()
    {
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
        Assert.Contains("unknown-set", (await Refused(() => host.InvokeAsync<string>("CreateRoom", HostKey, "nope"))).Message);
        await Open(host);
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
    public async Task Join_attempts_are_throttled_per_connection()
    {
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
        Assert.Equal("Joined", await first.InvokeAsync<string>("Join", code, "Ada"));

        var second = await Connect(factory);
        var seated = Seated(second);
        Assert.Equal("RoomFull", await second.InvokeAsync<string>("Join", code, "Bob"));
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
        foreach (var action in new[] { "StartRound", "CloseQuestion", "AdvanceQuestion" })
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
        foreach (var action in new[] { "StartRound", "CloseQuestion", "AdvanceQuestion" })
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
        var text = q.GetRawText();
        Assert.DoesNotContain("correct", text, StringComparison.OrdinalIgnoreCase);
        foreach (var property in q.EnumerateObject())
            Assert.DoesNotContain("correct", property.Name, StringComparison.OrdinalIgnoreCase);
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
}
