using System.Text;
using NPlusOne.Analyzers.Tests.Harness;
using NPlusOne.Analyzers.Tests.Harness.GroundTruth;
using Xunit.Abstractions;

namespace NPlusOne.Analyzers.Tests;

// Runs every corpus method once more, under the runtime guard (writes included), on 6 seeded rows per entity: the guard
// must report a suspect for every method the ground truth proves grows, and none for a method it proves constant. A
// method whose marker the ground truth cannot drive is not asserted. Shares a collection with GroundTruthTests: loading the
// same corpus folder twice at once trips over process-wide state (proxy types, Mapster's global settings).
[Collection(CorpusRuntime.Collection)]
public class RuntimeGuardTests(ITestOutputHelper output)
{
    static RuntimeGuardTests() => Console.SetOut(TextWriter.Null);

    private const int Rows = 6;

    public static TheoryData<string> Folders() => [.. Corpus.Folders()];

    [Theory]
    [MemberData(nameof(Folders))]
    public async Task The_guard_reports_what_grows_and_nothing_else(string folder)
    {
        var runtime = new CorpusRuntime(folder, new NPlusOneGuardDetector());
        var failures = new List<string>();
        var report = new StringBuilder();
        foreach (var method in runtime.Methods)
        {
            var run = await runtime.RunAsync(method, Rows);
            if (run.NotRun is not null || run.Suspects is null || method.Expectation == Expectation.Unasserted)
            {
                continue;
            }

            var line = $"{method} [{method.Expectation}] {run.Suspects.Count} suspects" + (run.Suspects.Count > 0 ? ": " + string.Join("; ", run.Suspects) : "");
            report.AppendLine(line);
            if (method.Expectation == Expectation.Grows && run.Suspects.Count == 0 || method.Expectation == Expectation.Constant && run.Suspects.Count > 0)
            {
                failures.Add(line);
            }
        }

        output.WriteLine(report.ToString());
        Assert.True(failures.Count == 0, $"{failures.Count} methods disagree with the guard:\n" + string.Join("\n", failures));
    }
}
