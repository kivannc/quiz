using Microsoft.Extensions.Time.Testing;
using Quiz.Game;

namespace Quiz.Tests;

public sealed class RoomStandingsTests
{
    [Fact]
    public void Standings_are_best_score_first_with_ties_in_join_order_exactly_as_the_reveal_reports_them()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        var options = Enumerable.Range(0, 4).Select(i => new Option((Marker)i, "o" + i)).ToList();
        var set = new QuestionSet("t", "T", "en", [new Question("q0", QuestionType.MultipleChoice, "Q?", options, Marker.Hearts)]);
        var room = new Room("1234", set, clock, 8, TimeSpan.FromSeconds(20));
        room.Join("Ada", "a");
        room.Join("Bob", "b");
        room.Join("Cy", "c");
        Assert.Equal(["Ada", "Bob", "Cy"], room.Standings.Select(p => p.Name));

        room.Start();
        room.Answer("c", Marker.Hearts); // only Cy scores

        var reveal = room.CloseQuestion();

        Assert.Equal(["Cy", "Ada", "Bob"], room.Standings.Select(p => p.Name));
        Assert.Equal(reveal.Standings, room.Standings);
    }

    private static Room StartedRoom(FakeTimeProvider clock)
    {
        var options = Enumerable.Range(0, 4).Select(i => new Option((Marker)i, "o" + i)).ToList();
        var set = new QuestionSet("t", "T", "en", [new Question("q0", QuestionType.MultipleChoice, "Q?", options, Marker.Hearts)]);
        var room = new Room("1234", set, clock, 8, TimeSpan.FromSeconds(20));
        room.Join("Ada", "a");
        room.Start();
        return room;
    }

    [Fact]
    public void CurrentReveal_is_null_outside_the_reveal_and_reading_it_never_closes_a_question()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        var room = StartedRoom(clock);

        Assert.Null(room.CurrentReveal);
        Assert.Equal(RoomPhase.Question, room.Phase); // reading did not close the open question
        Assert.True(room.Answer("a", Marker.Hearts)); // and answers are still accepted
    }

    [Fact]
    public void CurrentReveal_returns_the_reveal_being_shown_without_changing_anything()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        var room = StartedRoom(clock);
        room.Answer("a", Marker.Hearts);
        var closed = room.CloseQuestion();

        Assert.Same(closed, room.CurrentReveal);
        Assert.Equal(RoomPhase.Reveal, room.Phase);
        Assert.False(room.Advance()); // single-question set: advancing finishes the round
        Assert.Null(room.CurrentReveal);
    }
}
