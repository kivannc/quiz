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
}
