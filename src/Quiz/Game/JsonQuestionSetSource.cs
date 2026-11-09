using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quiz.Game;

/// <summary>
/// Reads every <c>*.json</c> question set in a directory. All validation happens in the
/// constructor, so a bad file fails at startup rather than in the middle of a round.
/// Every failure is an <see cref="InvalidOperationException"/> that names the offending file.
/// </summary>
public sealed class JsonQuestionSetSource : IQuestionSetSource
{
    private const int QuestionsPerSet = 10;
    private const int OptionsPerQuestion = 4;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly IReadOnlyList<QuestionSet> _sets;

    public JsonQuestionSetSource(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Question directory not found: {directory}");

        var sets = new List<QuestionSet>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var file = Path.GetFileName(path);
            var set = Load(path, file);
            if (sets.Any(s => s.Id == set.Id))
                throw Invalid(file, $"duplicate set id '{set.Id}'");
            sets.Add(set);
        }
        _sets = sets;
    }

    public IReadOnlyList<QuestionSet> All() => _sets;

    public QuestionSet? ById(string id) => _sets.FirstOrDefault(s => s.Id == id);

    private static QuestionSet Load(string path, string file)
    {
        SetDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SetDto>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{file}: {ex.Message}", ex);
        }

        if (dto is null) throw Invalid(file, "file is empty");
        if (string.IsNullOrWhiteSpace(dto.Id)) throw Invalid(file, "set id is missing");
        if (string.IsNullOrWhiteSpace(dto.Title)) throw Invalid(file, "set title is missing");
        if (string.IsNullOrWhiteSpace(dto.Language)) throw Invalid(file, "set language is missing");

        var questions = dto.Questions ?? [];
        if (questions.Count != QuestionsPerSet)
            throw Invalid(file, $"expected {QuestionsPerSet} questions, found {questions.Count}");

        var built = questions.Select(q => Build(q, file)).ToList();
        var duplicate = built.GroupBy(q => q.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw Invalid(file, $"duplicate question id '{duplicate.Key}'");

        return new QuestionSet(dto.Id, dto.Title, dto.Language, built);
    }

    private static Question Build(QuestionDto q, string file)
    {
        if (string.IsNullOrWhiteSpace(q.Id)) throw Invalid(file, "a question has no id");
        if (string.IsNullOrWhiteSpace(q.Text)) throw Invalid(file, $"question '{q.Id}' has no text");

        var options = q.Options ?? [];
        if (options.Count != OptionsPerQuestion)
            throw Invalid(file, $"question '{q.Id}' needs {OptionsPerQuestion} options, found {options.Count}");
        if (options.Select(o => o.Marker).Distinct().Count() != OptionsPerQuestion)
            throw Invalid(file, $"question '{q.Id}' must use each marker exactly once");
        if (options.Any(o => string.IsNullOrWhiteSpace(o.Text)))
            throw Invalid(file, $"question '{q.Id}' has an option with no text");
        if (q.Correct is not { } correct || options.All(o => o.Marker != correct))
            throw Invalid(file, $"question '{q.Id}' has a correct marker that is not among its options");

        return new Question(q.Id, q.Type, q.Text, options.Select(o => new Option(o.Marker, o.Text!)).ToList(), correct);
    }

    private static InvalidOperationException Invalid(string file, string problem) => new($"{file}: {problem}");

    // Deserialisation shapes: nullable so a missing field becomes a named validation error.
    private sealed class SetDto
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public string? Language { get; init; }
        public List<QuestionDto>? Questions { get; init; }
    }

    private sealed class QuestionDto
    {
        public string? Id { get; init; }
        public QuestionType Type { get; init; } = QuestionType.MultipleChoice;
        public string? Text { get; init; }
        public List<OptionDto>? Options { get; init; }
        public Marker? Correct { get; init; }
    }

    private sealed class OptionDto
    {
        public Marker Marker { get; init; }
        public string? Text { get; init; }
    }
}
