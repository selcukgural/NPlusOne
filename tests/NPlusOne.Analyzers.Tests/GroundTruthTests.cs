using System.Globalization;
using System.Text;
using NPlusOne.Analyzers.Tests.Harness;
using NPlusOne.Analyzers.Tests.Harness.GroundTruth;
using Xunit.Abstractions;

namespace NPlusOne.Analyzers.Tests;

// Proves the corpus markers at run time: every method runs on a real SQLite database seeded with 3 and
// then 6 rows per entity. A method with a warning marker must send more SQL commands for 6 than for 3
// (one round trip per element); a method without one the same number. A marked method that cannot run,
// or one whose element count does not drive it, says why in a // ground-truth: comment. NPLUSONE_SQL=1
// prints the SQL.
[Collection(CorpusRuntime.Collection)]
public class GroundTruthTests(ITestOutputHelper output)
{
    // The corpus writes what it reads to the console; keep the test output to the measurements.
    static GroundTruthTests() => Console.SetOut(TextWriter.Null);

    private const int Small = 3;
    private const int Large = 6;

    public static TheoryData<string> Folders() => [.. Corpus.Folders()];

    [Theory]
    [MemberData(nameof(Folders))]
    public async Task Marked_methods_send_more_commands_for_more_elements_and_unmarked_ones_do_not(string folder)
    {
        var runtime = new CorpusRuntime(folder);
        var failures = new List<string>();
        var notRun = new List<string>();
        var report = new StringBuilder();
        foreach (var method in runtime.Methods)
        {
            var small = await runtime.RunAsync(method, Small);
            var large = small.NotRun is null ? await runtime.RunAsync(method, Large) : small;
            if (small.NotRun is not null || large.NotRun is not null)
            {
                notRun.Add($"{method} [{method.Expectation}]: {small.NotRun ?? large.NotRun}");

                // A marked case nobody can run must say why, or its marker is unproven without anyone noticing.
                if (method.Ids.Count > 0 && method.NotDrivenByData is null)
                {
                    failures.Add($"{method} is marked but cannot run ({small.NotRun ?? large.NotRun}); fix the harness or explain with // ground-truth: reason");
                }

                continue;
            }

            var grows = large.Count > small.Count;
            var line = $"{method} [{method.Expectation}] {small.Count} -> {large.Count} commands" + (method.NotDrivenByData is { } reason ? $" ({reason})" : "");
            report.AppendLine(line);
            if (method.Expectation == Expectation.Grows && !grows || method.Expectation == Expectation.Constant && grows)
            {
                failures.Add(line);
            }

            if (Environment.GetEnvironmentVariable("NPLUSONE_SQL") is not null)
            {
                foreach (var command in small.Commands)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"      {command.ReplaceLineEndings(" ")}");
                }
            }
        }

        output.WriteLine(report.ToString());
        output.WriteLine($"Not run ({notRun.Count}):");
        foreach (var line in notRun)
        {
            output.WriteLine("  " + line);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} methods disagree with their markers:\n" + string.Join("\n", failures));
    }
}
