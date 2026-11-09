using Microsoft.Extensions.Time.Testing;
using Quiz.Game;

namespace Quiz.Tests;

public sealed class RoomTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    // Ten questions; question i is correct on Marker i % 4 so tests can pick right and wrong answers.
    private static QuestionSet TenQuestions()
    {
        var questions = Enumerable.Range(0, 10).Select(i => new Question(
            $"q{i}",
            QuestionType.MultipleChoice,
            $"Question {i}?",
            Enum.GetValues<Marker>().Select(m => new Option(m, $"{m} {i}")).ToList(),
            (Marker)(i % 4))).ToList();
        return new QuestionSet("test", "Test", "en", questions);
    }

    private Room NewRoom(int maxPlayers = 8) => new("ABCD", TenQuestions(), _clock, maxPlayers, Limit);

    private Room StartedRoom(params string[] names)
    {
        var room = NewRoom();
        foreach (var name in names) room.Join(name, "conn-" + name);
        Assert.True(room.Start());
        return room;
    }

    [Fact]
    public void Join_in_lobby_adds_a_player_with_zero_score()
    {
        var room = NewRoom();

        Assert.Equal(JoinResult.Joined, room.Join("Ada", "c1"));

        Assert.Equal(RoomPhase.Lobby, room.Phase);
        Assert.Equal(new[] { new PlayerView("Ada", 0) }, room.Players);
    }

    [Fact]
    public void Joining_again_with_the_same_name_rejoins_the_same_seat_keeping_the_score()
    {
        var room = StartedRoom("Ada", "Bob");
        room.Answer("conn-Ada", Marker.Hearts);   // q0 is correct on Hearts
        room.CloseQuestion();
        var scoreBefore = room.Players.Single(p => p.Name == "Ada").Score;
        Assert.True(scoreBefore > 0);

        Assert.Equal(JoinResult.Rejoined, room.Join("Ada", "conn-new"));

        Assert.Equal(2, room.Players.Count);
        Assert.Equal(scoreBefore, room.Players.Single(p => p.Name == "Ada").Score);
    }

    [Fact]
    public void Rejoining_mid_round_is_allowed_and_does_not_duplicate_the_player()
    {
        var room = StartedRoom("Ada");

        Assert.Equal(JoinResult.Rejoined, room.Join("Ada", "conn-new"));

        Assert.Single(room.Players);
        // The new connection now owns the seat; the old one no longer does.
        Assert.True(room.Answer("conn-new", Marker.Hearts));
        Assert.False(room.Answer("conn-Ada", Marker.Hearts));
    }

    [Theory]
    [InlineData("  Ada ")]
    [InlineData("ada")]
    [InlineData("ADA ")]
    public void Rejoining_with_whitespace_or_different_case_lands_in_the_same_seat_with_the_score_intact(string typedAgain)
    {
        var room = StartedRoom("Ada");
        room.Answer("conn-Ada", Marker.Hearts);
        room.CloseQuestion();

        Assert.Equal(JoinResult.Rejoined, room.Join(typedAgain, "conn-new"));

        Assert.Equal(new[] { new PlayerView("Ada", Scoring.MaxPoints) }, room.Players);   // first-seen spelling kept
        room.Advance();
        Assert.True(room.Answer("conn-new", Marker.Diamonds));
    }

    [Fact]
    public void A_name_is_stored_trimmed()
    {
        var room = NewRoom();

        room.Join("  Ada  ", "c1");

        Assert.Equal("Ada", room.Players.Single().Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_or_whitespace_name_throws(string name)
    {
        Assert.Throws<ArgumentException>(() => NewRoom().Join(name, "c1"));
    }

    [Fact]
    public void Rejoining_drops_the_old_connection_so_it_can_no_longer_answer()
    {
        var room = StartedRoom("Ada");
        room.Join("Ada", "conn-new");

        Assert.False(room.Answer("conn-Ada", Marker.Hearts));   // old connection, seat has not answered yet

        Assert.Equal(0, room.AnswerCount);
        Assert.True(room.Answer("conn-new", Marker.Hearts));
    }

    [Fact]
    public void An_undefined_marker_is_refused_and_does_not_wedge_the_round()
    {
        var room = StartedRoom("Ada", "Bob");
        room.Answer("conn-Bob", Marker.Hearts);

        Assert.False(room.Answer("conn-Ada", (Marker)99));

        Assert.Equal(1, room.AnswerCount);
        var reveal = room.CloseQuestion();
        Assert.Equal(RoomPhase.Reveal, room.Phase);
        Assert.Equal(1, reveal.Distribution[Marker.Hearts]);
    }

    [Fact]
    public void A_null_connectionId_throws_without_seating_anyone()
    {
        var room = NewRoom();

        Assert.Throws<ArgumentNullException>(() => room.Join("Ada", null!));

        Assert.Empty(room.Players);
    }

    [Fact]
    public void The_reveal_is_read_only_even_when_cast_back()
    {
        var room = StartedRoom("Ada");
        var reveal = room.CloseQuestion();

        Assert.False(reveal.Distribution is Dictionary<Marker, int>);
        Assert.False(reveal.Standings is List<PlayerView>);
        Assert.Throws<NotSupportedException>(() => ((IList<PlayerView>)reveal.Standings).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<Marker, int>)reveal.Distribution)[Marker.Hearts] = 5);
    }

    // ---- phase matrix: Task 5 guards duplicate host actions by checking Phase, so these are a contract ----

    private Room RoomIn(RoomPhase phase)
    {
        var room = NewRoom();
        room.Join("Ada", "conn-Ada");
        if (phase == RoomPhase.Lobby) return room;
        room.Start();
        if (phase == RoomPhase.Question) return room;
        room.CloseQuestion();
        if (phase == RoomPhase.Reveal) return room;
        while (room.Advance()) room.CloseQuestion();
        Assert.Equal(RoomPhase.Finished, room.Phase);
        return room;
    }

    [Theory]
    [InlineData(RoomPhase.Question)]
    [InlineData(RoomPhase.Reveal)]
    [InlineData(RoomPhase.Finished)]
    public void Start_is_refused_once_the_round_has_begun(RoomPhase phase)
    {
        var room = RoomIn(phase);

        Assert.False(room.Start());

        Assert.Equal(phase, room.Phase);
    }

    [Fact]
    public void Advance_in_Lobby_throws()
    {
        Assert.Throws<InvalidOperationException>(() => RoomIn(RoomPhase.Lobby).Advance());
    }

    [Fact]
    public void Advance_when_Finished_returns_false_and_stays_Finished()
    {
        var room = RoomIn(RoomPhase.Finished);

        Assert.False(room.Advance());

        Assert.Equal(RoomPhase.Finished, room.Phase);
    }

    [Fact]
    public void CloseQuestion_in_Finished_throws()
    {
        Assert.Throws<InvalidOperationException>(() => RoomIn(RoomPhase.Finished).CloseQuestion());
    }

    [Theory]
    [InlineData(RoomPhase.Reveal)]
    [InlineData(RoomPhase.Finished)]
    public void A_new_name_is_refused_with_RoundInProgress_in_Reveal_and_Finished(RoomPhase phase)
    {
        var room = RoomIn(phase);

        Assert.Equal(JoinResult.RoundInProgress, room.Join("Latecomer", "c2"));

        Assert.Single(room.Players);
    }

    [Fact]
    public void A_rejoin_is_allowed_when_Finished()
    {
        var room = RoomIn(RoomPhase.Finished);

        Assert.Equal(JoinResult.Rejoined, room.Join("Ada", "conn-new"));

        Assert.Single(room.Players);
    }

    [Fact]
    public void A_new_name_after_the_round_started_is_refused_with_RoundInProgress()
    {
        var room = StartedRoom("Ada");

        Assert.Equal(JoinResult.RoundInProgress, room.Join("Latecomer", "c2"));

        Assert.Single(room.Players);
    }

    [Fact]
    public void Join_beyond_maxPlayers_is_refused_with_RoomFull()
    {
        var room = NewRoom(maxPlayers: 2);
        room.Join("Ada", "c1");
        room.Join("Bob", "c2");

        Assert.Equal(JoinResult.RoomFull, room.Join("Cleo", "c3"));

        Assert.Equal(2, room.Players.Count);
    }

    [Fact]
    public void Start_moves_to_Question_and_exposes_the_first_question()
    {
        var room = NewRoom();
        room.Join("Ada", "c1");

        Assert.True(room.Start());

        Assert.Equal(RoomPhase.Question, room.Phase);
        var q = room.CurrentQuestion;
        Assert.NotNull(q);
        Assert.Equal(0, q.Index);
        Assert.Equal(10, q.Total);
        Assert.Equal("Question 0?", q.Text);
        Assert.Equal(20, q.LimitSeconds);
        Assert.Equal(4, q.Options.Count);
    }

    [Fact]
    public void Start_is_refused_when_there_are_no_players()
    {
        var room = NewRoom();

        Assert.False(room.Start());

        Assert.Equal(RoomPhase.Lobby, room.Phase);
        Assert.Null(room.CurrentQuestion);
    }

    [Fact]
    public void A_correct_answer_scores_by_elapsed_time()
    {
        var room = StartedRoom("Ada");

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(room.Answer("conn-Ada", Marker.Hearts));
        room.CloseQuestion();

        Assert.Equal(750, room.Players.Single().Score);
    }

    [Fact]
    public void A_wrong_answer_scores_nothing_but_is_counted()
    {
        var room = StartedRoom("Ada");

        Assert.True(room.Answer("conn-Ada", Marker.Spades));

        Assert.Equal(1, room.AnswerCount);
        room.CloseQuestion();
        Assert.Equal(0, room.Players.Single().Score);
    }

    [Fact]
    public void A_second_answer_from_the_same_connection_is_ignored()
    {
        var room = StartedRoom("Ada");
        Assert.True(room.Answer("conn-Ada", Marker.Spades));   // wrong, and first

        Assert.False(room.Answer("conn-Ada", Marker.Hearts));  // would have been right

        Assert.Equal(1, room.AnswerCount);
        room.CloseQuestion();
        Assert.Equal(0, room.Players.Single().Score);
    }

    [Fact]
    public void An_answer_after_the_limit_scores_nothing_even_before_CloseQuestion()
    {
        var room = StartedRoom("Ada");

        _clock.Advance(Limit + TimeSpan.FromMilliseconds(1));

        Assert.False(room.Answer("conn-Ada", Marker.Hearts));
        Assert.Equal(0, room.AnswerCount);
        Assert.Equal(RoomPhase.Question, room.Phase);   // nobody closed it; the room judged by the clock alone
        room.CloseQuestion();
        Assert.Equal(0, room.Players.Single().Score);
    }

    [Fact]
    public void An_answer_exactly_on_the_buzzer_is_accepted()
    {
        var room = StartedRoom("Ada");

        _clock.Advance(Limit);

        Assert.True(room.Answer("conn-Ada", Marker.Hearts));
        room.CloseQuestion();
        Assert.Equal(Scoring.MinPoints, room.Players.Single().Score);
    }

    [Fact]
    public void An_answer_outside_the_Question_phase_is_refused()
    {
        var room = NewRoom();
        room.Join("Ada", "conn-Ada");
        Assert.False(room.Answer("conn-Ada", Marker.Hearts));   // Lobby

        room.Start();
        room.CloseQuestion();
        Assert.False(room.Answer("conn-Ada", Marker.Hearts));   // Reveal
    }

    [Fact]
    public void An_answer_from_an_unknown_connection_is_refused()
    {
        var room = StartedRoom("Ada");

        Assert.False(room.Answer("stranger", Marker.Hearts));
        Assert.Equal(0, room.AnswerCount);
    }

    [Fact]
    public void CloseQuestion_reports_the_distribution_across_all_four_markers()
    {
        var room = StartedRoom("Ada", "Bob", "Cleo");
        room.Answer("conn-Ada", Marker.Hearts);
        room.Answer("conn-Bob", Marker.Hearts);
        room.Answer("conn-Cleo", Marker.Clubs);

        var reveal = room.CloseQuestion();

        Assert.Equal(RoomPhase.Reveal, room.Phase);
        Assert.Equal(Marker.Hearts, reveal.Correct);
        Assert.Equal(4, reveal.Distribution.Count);
        Assert.Equal(2, reveal.Distribution[Marker.Hearts]);
        Assert.Equal(0, reveal.Distribution[Marker.Diamonds]);
        Assert.Equal(1, reveal.Distribution[Marker.Clubs]);
        Assert.Equal(0, reveal.Distribution[Marker.Spades]);
    }

    [Fact]
    public void CloseQuestion_with_no_answers_reports_all_four_markers_at_zero()
    {
        var room = StartedRoom("Ada");

        var reveal = room.CloseQuestion();

        Assert.Equal(4, reveal.Distribution.Count);
        Assert.All(reveal.Distribution.Values, count => Assert.Equal(0, count));
    }

    [Fact]
    public void CloseQuestion_standings_are_ordered_by_score_descending()
    {
        var room = StartedRoom("Ada", "Bob", "Cleo");
        room.Answer("conn-Ada", Marker.Spades);                  // wrong: 0
        _clock.Advance(TimeSpan.FromSeconds(10));
        room.Answer("conn-Bob", Marker.Hearts);                  // 750
        room.Answer("conn-Cleo", Marker.Hearts);                 // 750, same instant

        var reveal = room.CloseQuestion();

        Assert.Equal(new[] { "Bob", "Cleo", "Ada" }, reveal.Standings.Select(p => p.Name));
        Assert.Equal(new[] { 750, 750, 0 }, reveal.Standings.Select(p => p.Score));
    }

    [Fact]
    public void Standings_ties_keep_join_order()
    {
        var room = StartedRoom("Zed", "Amy", "Max");   // join order is deliberately not alphabetical

        var reveal = room.CloseQuestion();             // all on zero

        Assert.Equal(new[] { "Zed", "Amy", "Max" }, reveal.Standings.Select(p => p.Name));
    }

    [Fact]
    public void CloseQuestion_called_twice_returns_the_same_reveal_without_scoring_twice()
    {
        var room = StartedRoom("Ada");
        room.Answer("conn-Ada", Marker.Hearts);

        var first = room.CloseQuestion();
        var second = room.CloseQuestion();

        Assert.Equal(first.Standings, second.Standings);
        Assert.Equal(Scoring.MaxPoints, room.Players.Single().Score);
    }

    [Fact]
    public void CloseQuestion_outside_a_question_throws()
    {
        var room = NewRoom();
        room.Join("Ada", "c1");

        Assert.Throws<InvalidOperationException>(() => room.CloseQuestion());
    }

    [Fact]
    public void Advance_walks_all_ten_questions_then_returns_false_and_finishes()
    {
        var room = StartedRoom("Ada");

        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(i, room.CurrentQuestion!.Index);
            room.CloseQuestion();
            Assert.True(room.Advance());
            Assert.Equal(RoomPhase.Question, room.Phase);
        }

        Assert.Equal(9, room.CurrentQuestion!.Index);
        room.CloseQuestion();
        Assert.False(room.Advance());
        Assert.Equal(RoomPhase.Finished, room.Phase);
        Assert.Null(room.CurrentQuestion);
    }

    [Fact]
    public void Advance_opens_a_fresh_question_with_a_new_start_time_and_no_answers()
    {
        var room = StartedRoom("Ada");
        room.Answer("conn-Ada", Marker.Hearts);
        _clock.Advance(Limit);
        room.CloseQuestion();

        Assert.True(room.Advance());

        Assert.Equal(0, room.AnswerCount);
        Assert.Equal("Question 1?", room.CurrentQuestion!.Text);
        // Only the new question's clock matters: this is well inside its limit, though 25s after Start.
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(room.Answer("conn-Ada", Marker.Diamonds));   // q1 is correct on Diamonds
    }

    [Fact]
    public void Advance_outside_Reveal_throws_unless_the_round_is_finished()
    {
        var room = StartedRoom("Ada");
        Assert.Throws<InvalidOperationException>(() => room.Advance());   // still in Question
    }

    [Fact]
    public void Every_accepted_call_moves_LastActivity_forward()
    {
        var room = NewRoom();
        var last = room.LastActivity;

        void Expect(Action act)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            act();
            Assert.True(room.LastActivity > last);
            last = room.LastActivity;
        }

        Expect(() => room.Join("Ada", "c1"));
        Expect(() => room.Join("Ada", "c2"));   // rejoin
        Expect(() => room.Start());
        Expect(() => room.Answer("c2", Marker.Hearts));
        Expect(() => room.CloseQuestion());
        Expect(() => room.Advance());
    }

    [Fact]
    public void Refused_calls_do_not_move_LastActivity()
    {
        var room = NewRoom(maxPlayers: 1);
        room.Join("Ada", "c1");
        var last = room.LastActivity;
        _clock.Advance(TimeSpan.FromSeconds(1));

        room.Join("Bob", "c2");           // RoomFull
        room.Answer("c1", Marker.Hearts); // wrong phase

        Assert.Equal(last, room.LastActivity);
    }
}
