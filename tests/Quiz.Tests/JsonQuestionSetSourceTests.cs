using Quiz.Game;

namespace Quiz.Tests;

public sealed class JsonQuestionSetSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("quiz-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly string[] Markers = ["hearts", "diamonds", "clubs", "spades"];

    private static string QuestionJson(string id, string correct = "hearts", string? type = null, int options = 4)
    {
        var opts = string.Join(",", Markers.Take(options).Select(m => $$"""{ "marker": "{{m}}", "text": "Seçenek {{m}}" }"""));
        var typeField = type is null ? "" : $$""" "type": "{{type}}", """;
        return $$"""{ "id": "{{id}}", {{typeField}} "text": "Soru {{id}}?", "options": [{{opts}}], "correct": "{{correct}}" }""";
    }

    private static string SetJson(string id, Func<int, string>? question = null, int count = 10)
    {
        question ??= i => QuestionJson($"{id}-{i}");
        var questions = string.Join(",", Enumerable.Range(1, count).Select(question));
        return $$"""{ "id": "{{id}}", "title": "Başlık {{id}}", "language": "tr", "questions": [{{questions}}] }""";
    }

    private string Write(string fileName, string json)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void All_returns_every_set_in_the_directory()
    {
        Write("a.json", SetJson("a"));
        Write("b.json", SetJson("b"));
        Write("c.json", SetJson("c"));

        var sets = new JsonQuestionSetSource(_dir).All();

        Assert.Equal(3, sets.Count);
    }

    [Fact]
    public void Every_shipped_set_can_fill_a_round_and_every_question_has_four_options()
    {
        var sets = new JsonQuestionSetSource(ShippedQuestions.Directory).All();

        Assert.Equal(11, sets.Count);
        foreach (var set in sets)
        {
            // A set is a pool, not a round: what matters is that it can fill one.
            Assert.True(
                set.Questions.Count >= Room.QuestionsPerRound,
                $"{set.Id} has {set.Questions.Count} questions, fewer than a round");
            Assert.All(set.Questions, q => Assert.Equal(4, q.Options.Count));
        }
    }

    [Fact]
    public void A_set_too_small_to_fill_a_round_throws_naming_the_file()
    {
        Write("thin.json", SetJson("thin", count: Room.QuestionsPerRound - 1));

        var ex = Assert.Throws<InvalidOperationException>(() => new JsonQuestionSetSource(_dir));

        Assert.Contains("thin.json", ex.Message);
        Assert.Contains($"at least {Room.QuestionsPerRound} questions, found {Room.QuestionsPerRound - 1}", ex.Message);
    }

    [Fact]
    public void A_set_larger_than_a_round_is_loaded_whole()
    {
        Write("deep.json", SetJson("deep", count: 40));

        var set = new JsonQuestionSetSource(_dir).ById("deep")!;

        Assert.Equal(40, set.Questions.Count);
    }

    [Fact]
    public void Each_shipped_question_has_exactly_one_correct_marker()
    {
        var sets = new JsonQuestionSetSource(ShippedQuestions.Directory).All();

        foreach (var q in sets.SelectMany(s => s.Questions))
            Assert.Single(q.Options, o => o.Marker == q.Correct);
    }

    // The four facts below hold the shipped content to rules that used to be kept by hand. They read
    // the sets through the public loader, the way the app does.

    /// <summary>Folds case the Turkish way (I/ı, İ/i) and collapses whitespace, so near-copies compare equal.</summary>
    private static string Normalise(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLower(new System.Globalization.CultureInfo("tr-TR"));

    [Fact]
    public void No_shipped_question_text_appears_twice()
    {
        var sets = new JsonQuestionSetSource(ShippedQuestions.Directory).All();

        var repeated = sets
            .SelectMany(s => s.Questions.Select(q => (Set: s.Id, q.Id, Text: Normalise(q.Text))))
            .GroupBy(q => q.Text)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" = ", g.Select(q => $"{q.Set}/{q.Id}")))
            .ToList();

        Assert.True(repeated.Count == 0, "Same question text: " + string.Join("; ", repeated));
    }

    [Fact]
    public void Each_shipped_set_spreads_the_correct_suit_evenly()
    {
        var sets = new JsonQuestionSetSource(ShippedQuestions.Directory).All();

        foreach (var set in sets)
        {
            // A suit that is right more often than the others is a suit worth guessing. One apart is
            // allowed, so a single question can be added or dropped without bringing three more.
            var counts = Enum.GetValues<Marker>()
                .Select(m => set.Questions.Count(q => q.Correct == m))
                .ToList();
            Assert.True(
                counts.Max() - counts.Min() <= 1,
                $"{set.Id}: correct suits are uneven ({string.Join("/", counts)} for hearts/diamonds/clubs/spades)");
        }
    }

    [Fact]
    public void Shipped_text_fits_the_television()
    {
        var sets = new JsonQuestionSetSource(ShippedQuestions.Directory).All();

        foreach (var q in sets.SelectMany(s => s.Questions))
        {
            Assert.True(q.Text.Length <= 90, $"{q.Id}: question is {q.Text.Length} characters, over 90");
            foreach (var o in q.Options)
                Assert.True(o.Text.Length <= 30, $"{q.Id}: option '{o.Text}' is over 30 characters");
        }
    }

    [Fact]
    public void No_shipped_question_repeats_an_option()
    {
        var sets = new JsonQuestionSetSource(ShippedQuestions.Directory).All();

        foreach (var q in sets.SelectMany(s => s.Questions))
            Assert.True(
                q.Options.Select(o => Normalise(o.Text)).Distinct().Count() == q.Options.Count,
                $"{q.Id}: two options read the same");
    }

    [Fact]
    public void Shipped_sets_are_tagged_tr()
    {
        var sets = new JsonQuestionSetSource(ShippedQuestions.Directory).All();

        Assert.All(sets, s => Assert.Equal("tr", s.Language));
    }

    [Fact]
    public void ById_returns_null_for_an_unknown_id()
    {
        Write("a.json", SetJson("a"));

        var source = new JsonQuestionSetSource(_dir);

        Assert.NotNull(source.ById("a"));
        Assert.Null(source.ById("nope"));
    }

    [Fact]
    public void Malformed_json_throws_naming_the_file()
    {
        Write("broken.json", "{ this is not json");

        var ex = Assert.Throws<InvalidOperationException>(() => new JsonQuestionSetSource(_dir));

        Assert.Contains("broken.json", ex.Message);
    }

    [Fact]
    public void A_question_with_too_few_options_throws()
    {
        Write("bad.json", SetJson("bad", i => QuestionJson($"bad-{i}", correct: "hearts", options: 2)));

        var ex = Assert.Throws<InvalidOperationException>(() => new JsonQuestionSetSource(_dir));

        Assert.Contains("bad.json", ex.Message);
        Assert.Contains("needs 4 options", ex.Message);
    }

    [Fact]
    public void An_option_with_no_marker_throws_naming_the_file()
    {
        Write("nomarker.json", SetJson("nomarker",
            i => QuestionJson($"nm-{i}").Replace("""{ "marker": "hearts", "text""", """{ "text""")));

        var ex = Assert.Throws<InvalidOperationException>(() => new JsonQuestionSetSource(_dir));

        Assert.Contains("nomarker.json", ex.Message);
        Assert.Contains("no marker", ex.Message);
    }

    [Fact]
    public void An_empty_directory_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new JsonQuestionSetSource(_dir));

        Assert.Contains("No question sets", ex.Message);
    }

    [Fact]
    public void A_question_with_four_valid_options_but_no_correct_marker_throws()
    {
        // Four options, four distinct markers: only the correct-marker rule is broken.
        // (With four distinct markers every marker is present, so "correct" can only be absent.)
        Write("nocorrect.json", SetJson("nocorrect", i => QuestionJson($"nc-{i}").Replace(", \"correct\": \"hearts\"", "")));

        var ex = Assert.Throws<InvalidOperationException>(() => new JsonQuestionSetSource(_dir));

        Assert.Contains("nocorrect.json", ex.Message);
        Assert.Contains("correct marker", ex.Message);
    }

    [Fact]
    public void A_missing_directory_throws()
    {
        var missing = Path.Combine(_dir, "does-not-exist");

        Assert.Throws<DirectoryNotFoundException>(() => new JsonQuestionSetSource(missing));
    }

    [Fact]
    public void A_question_with_no_type_defaults_to_MultipleChoice()
    {
        Write("a.json", SetJson("a"));

        var set = new JsonQuestionSetSource(_dir).ById("a")!;

        Assert.All(set.Questions, q => Assert.Equal(QuestionType.MultipleChoice, q.Type));
    }

    [Fact]
    public void A_question_of_an_unknown_type_throws_naming_the_file()
    {
        Write("odd.json", SetJson("odd", i => QuestionJson($"odd-{i}", type: "trueFalse")));

        var ex = Assert.Throws<InvalidOperationException>(() => new JsonQuestionSetSource(_dir));

        Assert.Contains("odd.json", ex.Message);
    }
}
