namespace Quiz.Game;

/// <summary>The four option markers. They are fixed and shown as card suits, never as letters.</summary>
public enum Marker { Hearts, Diamonds, Clubs, Spades }

/// <summary>The kind of question. v1 has a single member on purpose: this is the seam for later kinds.</summary>
public enum QuestionType { MultipleChoice }

public sealed record Option(Marker Marker, string Text);

public sealed record Question(
    string Id,
    QuestionType Type,
    string Text,
    IReadOnlyList<Option> Options,
    Marker Correct);

public sealed record QuestionSet(
    string Id,
    string Title,
    string Language,
    IReadOnlyList<Question> Questions);
