namespace Quiz.Tests;

/// <summary>Locates the real question sets shipped with the app, so their content is under test.</summary>
internal static class ShippedQuestions
{
    public static string Directory { get; } = Locate();

    private static string Locate()
    {
        // Walk up from the test output (bin/Debug/net10.0) to the repository root.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Quiz", "questions");
            if (System.IO.Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Could not find src/Quiz/questions above " + AppContext.BaseDirectory);
    }
}
