using Microsoft.Extensions.Time.Testing;
using Quiz.Game;

namespace Quiz.Tests;

public sealed class RoomStoreTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    private sealed class OneSet : IQuestionSetSource
    {
        private static readonly QuestionSet Set = new("test", "Test", "en",
        [
            new Question("q0", QuestionType.MultipleChoice, "Q?",
                Enum.GetValues<Marker>().Select(m => new Option(m, m.ToString())).ToList(), Marker.Hearts)
        ]);

        public IReadOnlyList<QuestionSet> All() => [Set];
        public QuestionSet? ById(string id) => id == Set.Id ? Set : null;
    }

    private RoomStore NewStore(int maxRooms = 5) =>
        new(new OneSet(), _clock, new RoomOptions(maxRooms, 8, Idle, Limit, 10));

    private static string CodeOf(CreateResult r) => Assert.IsType<CreateResult.Created>(r).Code;

    [Fact]
    public void Create_returns_a_four_digit_code()
    {
        var code = CodeOf(NewStore().Create("test"));

        Assert.Equal(4, code.Length);
        Assert.InRange(int.Parse(code), 1000, 9999);
    }

    [Fact]
    public void Remove_takes_a_room_out_and_frees_its_code()
    {
        var store = NewStore();
        var code = CodeOf(store.Create("test"));
        Assert.NotNull(store.Find(code));

        Assert.True(store.Remove(code));

        Assert.Null(store.Find(code));
        Assert.Equal(0, store.Count);
        Assert.False(store.Remove(code)); // it has already gone
    }

    [Fact]
    public void Created_room_can_be_found_by_its_code()
    {
        var store = NewStore();

        var code = CodeOf(store.Create("test"));

        Assert.Equal(code, store.Find(code)?.Code);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Codes_are_unique_across_live_rooms()
    {
        var store = NewStore(maxRooms: 200);

        var codes = Enumerable.Range(0, 200).Select(_ => CodeOf(store.Create("test"))).ToList();

        Assert.Equal(200, codes.Distinct().Count());
        Assert.Equal(200, store.Count);
    }

    [Fact]
    public void Create_beyond_MaxRooms_returns_TooManyRooms()
    {
        var store = NewStore(maxRooms: 2);
        store.Create("test");
        store.Create("test");

        Assert.IsType<CreateResult.TooManyRooms>(store.Create("test"));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Create_with_an_unknown_set_id_returns_UnknownSet()
    {
        var store = NewStore();

        Assert.IsType<CreateResult.UnknownSet>(store.Create("nope"));
        Assert.Equal(0, store.Count);
    }

    [Theory]
    [InlineData("0000")]
    [InlineData("9999x")]
    [InlineData("")]
    [InlineData(null)]
    public void Find_is_null_for_an_unknown_code(string? code)
    {
        var store = NewStore();
        store.Create("test");

        Assert.Null(store.Find(code!));
    }

    [Fact]
    public void SweepIdle_closes_rooms_idle_past_the_timeout_and_frees_their_codes()
    {
        var store = NewStore(maxRooms: 1);
        var code = CodeOf(store.Create("test"));

        _clock.Advance(Idle + TimeSpan.FromSeconds(1));
        var removed = store.SweepIdle();

        // The codes are reported, not just counted: whoever swept has to tell those rooms they ended.
        Assert.Equal(code, Assert.Single(removed));
        Assert.Equal(0, store.Count);
        Assert.Null(store.Find(code));
        // The freed slot is usable again.
        Assert.IsType<CreateResult.Created>(store.Create("test"));
    }

    [Fact]
    public void SweepIdle_keeps_a_room_exactly_at_the_timeout()
    {
        var store = NewStore();
        var code = CodeOf(store.Create("test"));

        _clock.Advance(Idle);

        Assert.Empty(store.SweepIdle());
        Assert.NotNull(store.Find(code));
    }

    [Fact]
    public void SweepIdle_keeps_a_room_whose_round_is_active()
    {
        var store = NewStore();
        var idleCode = CodeOf(store.Create("test"));
        var activeCode = CodeOf(store.Create("test"));
        var active = store.Find(activeCode)!;
        active.Join("Ada", "c1");

        _clock.Advance(Idle - TimeSpan.FromMinutes(1));
        Assert.True(active.Start());              // activity just before the sweep
        _clock.Advance(TimeSpan.FromMinutes(2));  // idle room is past the timeout; the active one is 2 min old

        Assert.Equal(idleCode, Assert.Single(store.SweepIdle()));
        Assert.Null(store.Find(idleCode));
        Assert.NotNull(store.Find(activeCode));
    }

    [Fact]
    public void A_pause_nobody_comes_back_from_is_swept_in_the_end()
    {
        var store = NewStore();
        var code = CodeOf(store.Create("test"));
        var room = store.Find(code)!;
        room.Join("Ada", "c1");
        Assert.True(room.Start());
        Assert.True(room.Pause());

        // Deep into the grace, still nobody's to reclaim.
        _clock.Advance(Room.PauseGrace - TimeSpan.FromMinutes(1));
        Assert.Empty(store.SweepIdle());
        Assert.NotNull(store.Find(code));

        // Past the grace it ages from the end of it, and goes one idle timeout after that.
        _clock.Advance(TimeSpan.FromMinutes(1) + Idle);
        Assert.Empty(store.SweepIdle()); // exactly at the timeout, which this store keeps
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(code, Assert.Single(store.SweepIdle()));
        Assert.Null(store.Find(code));
    }

    [Fact]
    public void A_paused_room_is_not_swept_however_long_the_pause_lasts()
    {
        var store = NewStore();
        var code = CodeOf(store.Create("test"));
        var room = store.Find(code)!;
        room.Join("Ada", "c1");
        Assert.True(room.Start());
        Assert.True(room.Pause());

        // Everyone has been away from the table for far longer than the idle timeout, and still
        // inside the grace a pause is given.
        _clock.Advance(Idle * 3);
        Assert.Empty(store.SweepIdle());
        Assert.NotNull(store.Find(code));

        // Once it is running again it ages like any other room.
        Assert.True(room.Resume());
        _clock.Advance(Idle * 3);
        Assert.Equal(code, Assert.Single(store.SweepIdle()));
        Assert.Null(store.Find(code));
    }
}

public sealed class RoomOptionsTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public void Valid_options_are_accepted()
    {
        var options = new RoomOptions(5, 8, Idle, Limit, 10);

        Assert.Equal(5, options.MaxRooms);
    }

    [Fact]
    public void MaxRooms_below_one_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomOptions(0, 8, Idle, Limit, 10));

    [Fact]
    public void MaxPlayers_below_one_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomOptions(5, 0, Idle, Limit, 10));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_IdleTimeout_is_rejected(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomOptions(5, 8, TimeSpan.FromSeconds(seconds), Limit, 10));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_QuestionLimit_is_rejected(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomOptions(5, 8, Idle, TimeSpan.FromSeconds(seconds), 10));

    [Theory]
    [InlineData(9001)]
    [InlineData(100000)]
    public void MaxRooms_beyond_the_code_space_is_rejected(int maxRooms) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomOptions(maxRooms, 8, Idle, Limit, 10));

    [Fact]
    public void MaxRooms_equal_to_the_code_space_is_accepted() =>
        Assert.Equal(9000, new RoomOptions(9000, 8, Idle, Limit, 10).MaxRooms);

    [Theory]
    [InlineData(10)]   // shorter than the question limit
    [InlineData(20)]   // equal to the question limit
    public void IdleTimeout_not_longer_than_QuestionLimit_is_rejected(int idleSeconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomOptions(5, 8, TimeSpan.FromSeconds(idleSeconds), Limit, 10));

    [Fact]
    public void Options_cannot_be_replaced_after_construction()
    {
        // No setters and no init accessors: validation cannot be stepped around.
        foreach (var p in typeof(RoomOptions).GetProperties())
            Assert.Null(p.SetMethod);
        Assert.Empty(typeof(RoomOptions).GetMethods().Where(m => m.Name == "<Clone>$"));
    }

    [Fact]
    public void JoinAttemptsPerMinute_below_one_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomOptions(5, 8, Idle, Limit, 0));
}
