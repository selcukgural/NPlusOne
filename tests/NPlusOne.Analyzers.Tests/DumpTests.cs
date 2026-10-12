using System.Globalization;
using NPlusOne.Analyzers.Tests.Harness;
using Xunit.Abstractions;

namespace NPlusOne.Analyzers.Tests;

// Prints every corpus diagnostic with its message, for reviewing message quality:
// NPLUSONE_DUMP=1 dotnet test --filter DumpTests --logger "console;verbosity=detailed"
public class DumpTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Prints_corpus_diagnostics_when_asked()
    {
        if (Environment.GetEnvironmentVariable("NPLUSONE_DUMP") is null)
        {
            return;
        }

        foreach (var folder in Corpus.Folders())
        {
            var options = Corpus.Options(folder);
            var excluded = options.TryGetValue("harness.exclude_references", out var list) ? list.Split(';') : [];
            var diagnostics = await TestCompilation.AnalyzeProjectsAsync(Corpus.Projects(folder), options, excluded);
            foreach (var d in diagnostics.OrderBy(d => d.Location.SourceTree!.FilePath, StringComparer.Ordinal).ThenBy(d => d.Location.SourceSpan.Start))
            {
                output.WriteLine($"{Path.GetFileName(d.Location.SourceTree!.FilePath)}:{d.Location.GetLineSpan().StartLinePosition.Line + 1} {d.Id} {d.GetMessage(CultureInfo.InvariantCulture)}");
            }
        }
    }
}
