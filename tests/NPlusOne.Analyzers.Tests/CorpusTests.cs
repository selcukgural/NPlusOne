using NPlusOne.Analyzers.Tests.Harness;

namespace NPlusOne.Analyzers.Tests;

public class CorpusTests
{
    public static TheoryData<string> Folders() => [.. Corpus.Folders()];

    [Theory]
    [MemberData(nameof(Folders))]
    public async Task Every_marked_line_is_reported_and_nothing_else(string folder)
    {
        var projects = Corpus.Projects(folder);
        var options = Corpus.Options(folder);
        var excluded = options.TryGetValue("harness.exclude_references", out var list)
            ? list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        var diagnostics = await TestCompilation.AnalyzeProjectsAsync(projects, options, excluded);

        ExpectedDiagnostics.Verify([.. projects.SelectMany(p => p.Sources)], diagnostics);
    }
}

public class DeterminismTests
{
    public static TheoryData<string> Folders() => [.. Corpus.Folders()];

    // Callbacks run concurrently and summaries are cached on first use; the result must not depend on the order.
    [Theory]
    [MemberData(nameof(Folders))]
    public async Task Twenty_runs_report_the_same_diagnostics(string folder)
    {
        var projects = Corpus.Projects(folder);
        var options = Corpus.Options(folder);
        var excluded = options.TryGetValue("harness.exclude_references", out var list) ? list.Split(';') : [];

        static string Render(IEnumerable<Microsoft.CodeAnalysis.Diagnostic> diagnostics)
            => string.Join("\n", diagnostics.Select(d => $"{d.Location.GetLineSpan()} {d.Id} {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}").Order(StringComparer.Ordinal));

        var first = Render(await TestCompilation.AnalyzeProjectsAsync(projects, options, excluded));
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(first, Render(await TestCompilation.AnalyzeProjectsAsync(projects, options, excluded)));
        }
    }
}
