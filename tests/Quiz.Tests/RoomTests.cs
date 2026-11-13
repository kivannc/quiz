using Microsoft.Extensions.Time.Testing;
using Quiz.Game;

namespace Quiz.Tests;

public sealed class RoomTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    // Ten questions, every one of them correct on Hearts. A room draws its round out of the set it is
    // given (see Room.QuestionsPerRound), so a test cannot know which question it is looking at - only
    // that Hearts answers it and the other three markers do not. Pool(count, varyCorrect: true) marks
    // the questions apart again, for the tests that have to tell them apart.
    private static QuestionSet TenQuestions() => Pool(10);

    /// <summary>
    /// A set of <paramref name="count"/> questions, each "Question i?" with all four markers as options.
    /// Correct on Hearts throughout, or, with <paramref name="varyCorrect"/>, question i correct on
    /// marker i % 4 - so a reveal that names the wrong question's marker can be caught.
    /// </summary>
    private static QuestionSet Pool(int count, bool varyCorrect = false)
    {
        var questions = Enumerable.Range(0, count).Select(i => new Question(
            $"q{i}",
            QuestionType.MultipleChoice,
            $"Question {i}?",
            Enum.GetValues<Marker>().Select(m => new Option(m, $"{m} {i}")).ToList(),
            varyCorrect ? (Marker)(i % 4) : Marker.Hearts)).ToList();
        return new QuestionSet("test", "Test", "en", questions);
    }

    private Room NewRoom(int maxPlayers = 8) => new("ABCD", TenQuestions(), _clock, maxPlayers, Limit);

    private Room StartedRoom(params string[] names) => StartedRoom(TenQuestions(), names);

    private Room StartedRoom(QuestionSet set, params string[] names)
    {
        var room = new Room("ABCD", set, _clock, 8, Limit);
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
        room.Answer("conn-Ada", Marker.Hearts);   // Hearts answers whichever question was drawn
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
        var steps = 0;
        while (room.Advance())
        {
            Assert.True(++steps <= Room.QuestionsPerRound, "the round would not end");
            room.CloseQuestion();
        }
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
        Assert.Contains(q.Text, TenQuestions().Questions.Select(x => x.Text));
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
    public void CloseQuestion_names_the_question_it_closes()
    {
        var room = StartedRoom("Ada");

        Assert.Equal(0, room.CloseQuestion().Index);
        Assert.True(room.Advance());
        Assert.Equal(1, room.CloseQuestion().Index);
        Assert.True(room.Advance());
        Assert.Equal(2, room.CloseQuestion().Index);

        // The same counting QuestionStarted uses, because the phone matches one against the other to
        // know whether the total it kept belongs to the question being revealed.
        Assert.Equal(2, room.CurrentQuestion!.Index);
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
        Assert.Contains(room.CurrentQuestion!.Text, TenQuestions().Questions.Select(x => x.Text));
        // Only the new question's clock matters: this is well inside its limit, though 25s after Start.
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(room.Answer("conn-Ada", Marker.Diamonds));
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
    public void Remaining_counts_the_question_down_and_is_clamped_at_zero()
    {
        var room = StartedRoom("Ada");
        Assert.Equal(Limit, room.Remaining);

        _clock.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(TimeSpan.FromSeconds(12), room.Remaining);

        _clock.Advance(Limit); // well past the limit
        Assert.Equal(TimeSpan.Zero, room.Remaining);

        // Reading the time does not move the room on, and the question is still answerable-or-not by the room's own rule.
        Assert.Equal(RoomPhase.Question, room.Phase);
        Assert.False(room.Answer("conn-Ada", Marker.Hearts));
    }

    [Theory]
    [InlineData(RoomPhase.Lobby)]
    [InlineData(RoomPhase.Reveal)]
    [InlineData(RoomPhase.Finished)]
    public void Remaining_is_null_when_no_question_is_open(RoomPhase phase)
    {
        Assert.Null(RoomIn(phase).Remaining);
    }

    [Fact]
    public void Paused_time_does_not_count_towards_the_question()
    {
        var room = StartedRoom("Ada");
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True(room.Pause());
        Assert.True(room.Paused);
        Assert.Equal(TimeSpan.FromSeconds(15), room.Remaining);

        // Away from the table for far longer than the whole limit.
        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(TimeSpan.FromSeconds(15), room.Remaining);
        Assert.Equal(RoomPhase.Question, room.Phase);

        Assert.True(room.Resume());
        Assert.False(room.Paused);
        Assert.Equal(TimeSpan.FromSeconds(15), room.Remaining);

        // And it runs down from there, not from the minutes that went by.
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(5), room.Remaining);
    }

    [Fact]
    public void An_answer_during_a_pause_is_refused_and_counts_again_once_resumed()
    {
        var room = StartedRoom("Ada");
        Assert.True(room.Pause());

        Assert.False(room.Answer("conn-Ada", Marker.Hearts)); // right marker, refused all the same

        room.Resume();
        Assert.True(room.Answer("conn-Ada", Marker.Hearts));
    }

    [Fact]
    public void A_question_resumed_after_a_long_pause_scores_by_the_time_it_actually_ran()
    {
        var room = StartedRoom("Ada");

        _clock.Advance(TimeSpan.FromSeconds(4));
        room.Pause();
        _clock.Advance(TimeSpan.FromMinutes(10)); // a long wait, in the middle of the question
        room.Resume();
        _clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(room.Answer("conn-Ada", Marker.Hearts));
        room.CloseQuestion();

        // Six seconds of question ran, so that is what it is worth — not ten minutes, which would
        // be long past the limit and worth nothing at all.
        Assert.Equal(Scoring.Points(correct: true, TimeSpan.FromSeconds(6), Limit), room.Players.Single().Score);
        Assert.True(room.Players.Single().Score > Scoring.MinPoints);
    }

    [Fact]
    public void Pausing_is_only_for_an_open_question_and_resuming_twice_is_harmless()
    {
        Assert.False(RoomIn(RoomPhase.Lobby).Pause());
        Assert.False(RoomIn(RoomPhase.Reveal).Pause());
        Assert.False(RoomIn(RoomPhase.Finished).Pause());

        var room = StartedRoom("Ada");
        Assert.False(room.Resume());  // not paused
        Assert.True(room.Pause());
        Assert.False(room.Pause());   // already paused
        Assert.True(room.Resume());
        Assert.False(room.Resume());
    }

    [Fact]
    public void Closing_a_paused_question_leaves_the_room_running()
    {
        var room = StartedRoom("Ada");
        room.Pause();

        room.CloseQuestion();

        Assert.False(room.Paused);
        Assert.True(room.Advance());
        Assert.False(room.Paused);
        Assert.Equal(Limit, room.Remaining); // the next question starts with its whole limit
    }

    [Fact]
    public void Pausing_and_resuming_count_as_activity()
    {
        var room = StartedRoom("Ada");
        var last = room.LastActivity;

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(room.Pause());
        Assert.True(room.LastActivity > last);

        last = room.LastActivity;
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(room.Resume());
        Assert.True(room.LastActivity > last);
    }

    [Fact]
    public void A_paused_room_stays_active_for_as_long_as_it_is_paused()
    {
        var room = StartedRoom("Ada");
        Assert.True(room.Pause());
        var atThePause = room.LastActivity;

        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(_clock.GetUtcNow(), room.LastActivity); // still being waited for, not abandoned
        Assert.True(room.LastActivity > atThePause);

        Assert.True(room.Resume());
        var atTheResume = room.LastActivity;
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(atTheResume, room.LastActivity);        // running again, and ageing again
    }

    [Fact]
    public void A_pause_holds_the_room_open_for_the_grace_and_no_longer()
    {
        var room = StartedRoom("Ada");
        Assert.True(room.Pause());
        var pausedAt = _clock.GetUtcNow();

        // Still being waited for, right up to the end of the grace.
        _clock.Advance(Room.PauseGrace);
        Assert.Equal(_clock.GetUtcNow(), room.LastActivity);

        // Past it, the room is as old as the end of its grace, and goes on ageing from there: a pause
        // nobody ever came back to must not hold a room slot and its code for the life of the process.
        _clock.Advance(TimeSpan.FromHours(8));
        Assert.Equal(pausedAt + Room.PauseGrace, room.LastActivity);
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

    /// <summary>
    /// Walks a started room to the end, returning the text of every question it showed, in order.
    /// Bounded, because a round cannot be longer than a round: a room that stopped advancing would
    /// otherwise hang the suite rather than fail it, and a run that never ends is the one failure that
    /// does not look like one - there is no CI here to notice it.
    /// </summary>
    private static List<string> QuestionsShown(Room room)
    {
        var shown = new List<string>();
        while (room.CurrentQuestion is { } q)
        {
            Assert.True(shown.Count <= Room.QuestionsPerRound, "the round would not end");
            shown.Add(q.Text);
            room.CloseQuestion();
            if (!room.Advance()) break;
        }
        return shown;
    }

    [Fact]
    public void A_round_plays_ten_of_a_larger_pool_and_no_question_twice()
    {
        var room = StartedRoom(Pool(40), "Ada");

        Assert.Equal(Room.QuestionsPerRound, room.CurrentQuestion!.Total);

        var shown = QuestionsShown(room);

        Assert.Equal(Room.QuestionsPerRound, shown.Count);
        Assert.Equal(shown.Count, shown.Distinct().Count());
        Assert.Subset(Pool(40).Questions.Select(q => q.Text).ToHashSet(), shown.ToHashSet());
        Assert.Equal(RoomPhase.Finished, room.Phase);
    }

    [Fact]
    public void A_pool_smaller_than_a_round_plays_whole()
    {
        var room = StartedRoom(Pool(3), "Ada");

        Assert.Equal(3, room.CurrentQuestion!.Total);

        Assert.Equal(3, QuestionsShown(room).Count);
        Assert.Equal(RoomPhase.Finished, room.Phase);
    }

    [Fact]
    public void Two_rooms_sharing_one_set_draw_differently_and_leave_it_as_they_found_it()
    {
        // The same QuestionSet instance both times, which is what RoomStore hands out: every room on a
        // category gets the one set object. A draw that shuffled in place would be invisible today -
        // QuestionSet.Questions is an IReadOnlyList over a List at runtime, so it is one cast away -
        // and would quietly reorder every other room's questions.
        var shared = Pool(40);
        var order = shared.Questions.Select(q => q.Text).ToList();

        var first = QuestionsShown(StartedRoom(shared, "Ada"));
        var second = QuestionsShown(StartedRoom(shared, "Bob"));

        Assert.Equal(order, shared.Questions.Select(q => q.Text));

        // Forty questions, ten drawn: the odds of two rounds drawing the same ten in the same order are
        // one in forty-permute-ten, which is a few thousand million million. A repeat means it is not drawing.
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_right_answer_scores_against_the_question_that_was_shown()
    {
        // Question i answers to marker i % 4 here, so answering the marker of the question the room has
        // just reported only ever scores if the room scores the question it showed. A room that scored
        // the set's question at the same index would mark three of every four of these wrong, and the
        // scoring path is the one this whole change risks getting wrong.
        var pool = Pool(40, varyCorrect: true);
        var correctOf = pool.Questions.ToDictionary(q => q.Text, q => q.Correct);
        var room = StartedRoom(pool, "Ada");

        var score = 0;
        for (var asked = 1; ; asked++)
        {
            Assert.True(asked <= Room.QuestionsPerRound, "the round would not end");
            var shown = room.CurrentQuestion!;
            Assert.True(room.Answer("conn-Ada", correctOf[shown.Text]));
            room.CloseQuestion();

            var now = room.Players.Single().Score;
            Assert.True(now > score, $"question {asked} of the round scored nothing");
            score = now;

            if (!room.Advance()) break;
        }

        Assert.Equal(RoomPhase.Finished, room.Phase);
    }

    [Fact]
    public void Every_reveal_names_the_correct_marker_of_the_question_that_was_shown()
    {
        // Question i is correct on marker i % 4 here, so a room that answered from the set instead of
        // from its own draw would name a marker belonging to some other question.
        var pool = Pool(40, varyCorrect: true);
        var correctOf = pool.Questions.ToDictionary(q => q.Text, q => q.Correct);
        var room = StartedRoom(pool, "Ada");

        for (var asked = 1; ; asked++)
        {
            Assert.True(asked <= Room.QuestionsPerRound, "the round would not end");
            var shown = room.CurrentQuestion!;
            var reveal = room.CloseQuestion();
            Assert.Equal(correctOf[shown.Text], reveal.Correct);
            if (!room.Advance()) break;
        }

        Assert.Equal(RoomPhase.Finished, room.Phase);
    }

    // ---- another round in the same room ----

    /// <summary>Plays the open round to its end, answering nothing, and returns the questions it asked in order.</summary>
    private static List<string> PlayOut(Room room)
    {
        var asked = new List<string>();
        // Bounded by the size of a round: a round that will not end must fail here, not hang.
        for (var step = 0; step <= Room.QuestionsPerRound; step++)
        {
            if (room.Phase == RoomPhase.Finished) return asked;
            asked.Add(room.CurrentQuestion!.Text);
            room.CloseQuestion();
            room.Advance();
        }
        Assert.Fail("the round never finished");
        return asked;
    }

    [Fact]
    public void A_new_round_is_refused_until_the_round_before_it_has_finished()
    {
        var room = NewRoom();
        room.Join("Ada", "c1");
        Assert.False(room.NewRound(TenQuestions()));          // lobby

        room.Start();
        Assert.False(room.NewRound(TenQuestions()));          // question
        room.CloseQuestion();
        Assert.False(room.NewRound(TenQuestions()));          // reveal

        Assert.Equal(RoomPhase.Reveal, room.Phase);
    }

    [Fact]
    public void A_new_round_keeps_the_players_in_their_seats_and_starts_them_from_nothing()
    {
        var room = StartedRoom("Ada", "Bob");
        room.Answer("conn-Ada", Marker.Hearts);
        PlayOut(room);
        Assert.True(room.Players.Single(p => p.Name == "Ada").Score > 0);

        Assert.True(room.NewRound(TenQuestions()));

        Assert.Equal(RoomPhase.Lobby, room.Phase);
        Assert.Equal(new[] { new PlayerView("Ada", 0), new PlayerView("Bob", 0) }, room.Players);
        Assert.Null(room.CurrentQuestion);
        Assert.Null(room.CurrentReveal);

        // The same connections still answer for the same seats: nobody has to join again.
        Assert.True(room.Start());
        Assert.True(room.Answer("conn-Bob", Marker.Hearts));
        room.CloseQuestion();
        Assert.True(room.Players.Single(p => p.Name == "Bob").Score > 0);
    }

    [Fact]
    public void A_new_round_lets_a_latecomer_in_before_it_starts()
    {
        var room = StartedRoom("Ada");
        Assert.Equal(JoinResult.RoundInProgress, room.Join("Late", "c-late"));
        PlayOut(room);
        Assert.Equal(JoinResult.RoundInProgress, room.Join("Late", "c-late"));

        room.NewRound(TenQuestions());

        Assert.Equal(JoinResult.Joined, room.Join("Late", "c-late"));
    }

    [Fact]
    public void Every_round_is_numbered_so_a_screen_can_tell_question_one_from_question_one()
    {
        var room = StartedRoom("Ada");
        Assert.Equal(1, room.CurrentQuestion!.Round);
        Assert.Equal(1, room.CloseQuestion().Round);
        room.Advance();
        PlayOut(room);

        room.NewRound(TenQuestions());
        room.Start();

        Assert.Equal(0, room.CurrentQuestion!.Index);
        Assert.Equal(2, room.CurrentQuestion!.Round);
        Assert.Equal(2, room.CloseQuestion().Round);
    }

    [Fact]
    public void A_new_round_can_be_played_from_another_set()
    {
        var room = StartedRoom("Ada");
        PlayOut(room);
        var other = new QuestionSet("other", "Other", "en", Enumerable.Range(0, 10).Select(i => new Question(
            $"o{i}", QuestionType.MultipleChoice, $"Other {i}?",
            Enum.GetValues<Marker>().Select(m => new Option(m, $"{m} {i}")).ToList(), Marker.Spades)).ToList());

        room.NewRound(other);
        room.Start();

        Assert.All(PlayOut(room), text => Assert.StartsWith("Other ", text));
    }

    [Fact]
    public void Rounds_in_one_room_do_not_repeat_a_question_until_the_set_has_run_out()
    {
        // Forty questions are four rounds of ten: played in one room, they must be forty different ones.
        var set = Pool(40);
        var room = StartedRoom(set, "Ada");
        var asked = PlayOut(room);
        for (var round = 2; round <= 4; round++)
        {
            Assert.True(room.NewRound(set));
            Assert.True(room.Start());
            asked.AddRange(PlayOut(room));
        }
        Assert.Equal(40, asked.Count);
        Assert.Equal(40, asked.Distinct().Count());

        // The fifth round has nothing new left to ask, and is played anyway rather than refused.
        Assert.True(room.NewRound(set));
        Assert.True(room.Start());
        var fifth = PlayOut(room);
        Assert.Equal(Room.QuestionsPerRound, fifth.Distinct().Count());
    }

    [Fact]
    public void A_set_that_runs_out_mid_draw_asks_what_is_left_before_anything_twice()
    {
        // Fifteen questions: the second round has five new ones, and must ask all five of them.
        var set = Pool(15);
        var room = StartedRoom(set, "Ada");
        var first = PlayOut(room);

        room.NewRound(set);
        room.Start();
        var second = PlayOut(room);

        var unseen = set.Questions.Select(q => q.Text).Except(first).ToList();
        Assert.Equal(5, unseen.Count);
        Assert.Subset(second.ToHashSet(), unseen.ToHashSet());
        Assert.Equal(Room.QuestionsPerRound, second.Distinct().Count());
    }

    [Fact]
    public void A_set_that_has_run_out_starts_over_so_the_round_after_is_fresh_again()
    {
        // Twenty questions. Rounds one and two ask all of them; round three has to repeat ten. If the
        // room then went on treating every question as asked, round four would be any ten at all -
        // it must be exactly the ten that round three did not repeat.
        var set = Pool(20);
        var room = StartedRoom(set, "Ada");
        PlayOut(room);
        room.NewRound(set);
        room.Start();
        PlayOut(room);

        room.NewRound(set);
        room.Start();
        var third = PlayOut(room);
        room.NewRound(set);
        room.Start();
        var fourth = PlayOut(room);

        Assert.Equal(20, third.Concat(fourth).Distinct().Count());
    }

    [Fact]
    public void What_one_set_has_asked_does_not_count_against_another_set_that_numbers_its_questions_the_same()
    {
        // Question ids are only unique inside a set, and here both sets use q0, q1, ... A round from
        // the small one in between must not make the big one think ten of its own have been asked:
        // the big set's second round is the ten it has not asked, with nothing from its first.
        var big = Pool(20);
        var small = new QuestionSet("small", "Small", "en", Enumerable.Range(0, 10).Select(i => new Question(
            $"q{i}", QuestionType.MultipleChoice, $"Small {i}?",
            Enum.GetValues<Marker>().Select(m => new Option(m, $"{m} {i}")).ToList(), Marker.Hearts)).ToList());
        var room = StartedRoom(big, "Ada");
        var first = PlayOut(room);

        room.NewRound(small);
        room.Start();
        PlayOut(room);
        room.NewRound(big);
        room.Start();
        var second = PlayOut(room);

        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void A_seat_is_told_what_it_answered_and_only_while_the_question_is_open()
    {
        var room = NewRoom();
        room.Join("Ada", "c1");
        room.Join("Bob", "c2");
        Assert.True(room.HasSeat("c1"));
        Assert.False(room.HasSeat("nobody"));
        Assert.Null(room.AnswerOf("c1"));   // the lobby asks nothing

        room.Start();
        Assert.Null(room.AnswerOf("c1"));
        Assert.True(room.Answer("c1", Marker.Clubs));
        Assert.Equal(Marker.Clubs, room.AnswerOf("c1"));
        Assert.Null(room.AnswerOf("c2"));
        Assert.Null(room.AnswerOf("nobody"));

        // The seat moves to another connection and takes its answer with it.
        Assert.Equal(JoinResult.Rejoined, room.Join("Ada", "c1-new"));
        Assert.False(room.HasSeat("c1"));
        Assert.Null(room.AnswerOf("c1"));
        Assert.Equal(Marker.Clubs, room.AnswerOf("c1-new"));

        room.CloseQuestion();
        Assert.Null(room.AnswerOf("c1-new"));
        room.Advance();
        Assert.Null(room.AnswerOf("c1-new"));   // a new question, nothing answered yet
    }
}
