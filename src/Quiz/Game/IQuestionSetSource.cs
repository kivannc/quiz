namespace Quiz.Game;

/// <summary>Where question sets come from. The game only ever talks to this interface.</summary>
public interface IQuestionSetSource
{
    IReadOnlyList<QuestionSet> All();

    QuestionSet? ById(string id);
}
