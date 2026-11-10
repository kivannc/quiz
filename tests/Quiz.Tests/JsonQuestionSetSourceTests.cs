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
    public void Each_shipped_set_has_ten_questions_of_four_options()
    {
        var sets = new JsonQuestionSetSource(ShippedQuestions.Directory).All();

        Assert.Equal(9, sets.Count);
        foreach (var set in sets)
        {
            Assert.Equal(10, set.Questions.Count);
            Assert.All(set.Questions, q => Assert.Equal(4, q.Options.Count));
        }
    }

    [Fact]
    public void Each_shipped_question_has_exactly_one_correct_marker()
    {
        var sets = new JsonQuestionSetSource(ShippedQuestions.Directory).All();

        foreach (var q in sets.SelectMany(s => s.Questions))
            Assert.Single(q.Options, o => o.Marker == q.Correct);
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
