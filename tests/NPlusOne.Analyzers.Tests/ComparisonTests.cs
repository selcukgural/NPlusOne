using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NPlusOne.Analyzers.Tests.Harness;
using NPlusOne.Analyzers.Tests.Harness.GroundTruth;
using Xunit.Abstractions;

namespace NPlusOne.Analyzers.Tests;

// Runs other N+1 analyzers over the corpus and scores them against the ground truth, method by method, next to this
// one: NPLUSONE_COMPARE=1 dotnet test --filter ComparisonTests --logger "console;verbosity=detailed". A method whose
// markers the ground truth proves (more SQL for more rows) counts as an N+1 to find, a method without markers as one
// that must stay quiet. The corpus was written for this analyzer, so its own score is high by construction; what the
// comparison shows is which of these cases another tool sees.
[Collection(CorpusRuntime.Collection)]
public partial class ComparisonTests(ITestOutputHelper output)
{
    // Runs every corpus method on 6 seeded rows per entity under each runtime detector with its default settings, and
    // scores it against the ground truth the same way: a method proven to grow should be reported, one proven constant not.
    [Fact]
    public async Task Scores_the_runtime_detectors_on_the_corpus_when_asked()
    {
        if (Environment.GetEnvironmentVariable("NPLUSONE_COMPARE") is null)
        {
            return;
        }

        IRuntimeDetector[] detectors = [new NPlusOneGuardDetector(), new QueryGuardDetector(), new DbEyeDetector(), new MiniProfilerDetector()];
        var report = new StringBuilder();
        foreach (var detector in detectors)
        {
            var (found, missed, alarms, quiet, notRun) = (0, 0, 0, 0, 0);
            var byRule = new Dictionary<string, (int Found, int Total)>();
            var flagged = new List<string>();
            foreach (var folder in Corpus.Folders())
            {
                var runtime = new CorpusRuntime(folder, detector);
                foreach (var method in runtime.Methods)
                {
                    if (method.Expectation == Expectation.Unasserted)
                    {
                        continue;
                    }

                    var run = await runtime.RunAsync(method, 6);
                    if (run.NotRun is not null || run.Suspects is null)
                    {
                        notRun++;
                        continue;
                    }

                    var hit = run.Suspects.Count > 0;
                    if (method.Expectation == Expectation.Grows)
                    {
                        (found, missed) = hit ? (found + 1, missed) : (found, missed + 1);
                        if (!hit)
                        {
                            flagged.Add($"missed: {method} [{string.Join(",", method.Ids)}]");
                        }
                        foreach (var id in method.Ids.Where(Growing.Contains).Distinct())
                        {
                            var (f, t) = byRule.GetValueOrDefault(id);
                            byRule[id] = (f + (hit ? 1 : 0), t + 1);
                        }
                    }
                    else
                    {
                        quiet++;
                        if (hit)
                        {
                            alarms++;
                            flagged.Add($"{method}: {run.Suspects[0]}");
                        }
                    }
                }
            }

            report.AppendLine(CultureInfo.InvariantCulture, $"{detector.Key}: found {found} of {found + missed} proven N+1 methods; flagged {alarms} of {quiet} proven-constant methods; {notRun} not run");
            foreach (var (id, (f, t)) in byRule.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"    {id}: {f} of {t}");
            }

            // Every false alarm, and the first missed methods (a tool that misses most of them would fill pages).
            foreach (var line in flagged.Where(l => !l.StartsWith("missed", StringComparison.Ordinal)).Concat(flagged.Where(l => l.StartsWith("missed", StringComparison.Ordinal)).Take(12)))
            {
                var shown = line.StartsWith("missed", StringComparison.Ordinal) ? line : "flagged, proven constant: " + line;
                report.AppendLine("    " + (shown.Length > 220 ? shown[..220] : shown));
            }
        }

        output.WriteLine(report.ToString());
    }

    private static readonly HashSet<string> Growing = ["NP0001", "NP0002", "NP0003", "NP0005", "NP0007"];

    // LinqContraband's N+1 rules: database execution in a loop, SaveChanges in a loop, repeated SaveChanges, navigation
    // read without Include.
    private static readonly HashSet<string> LinqContrabandRules = ["LC007", "LC010", "LC039", "LC045"];

    [Fact]
    public async Task Scores_the_analyzers_on_the_corpus_when_asked()
    {
        if (Environment.GetEnvironmentVariable("NPLUSONE_COMPARE") is null)
        {
            return;
        }

        var competitor = Path.Combine(AppContext.BaseDirectory, "competitors", "LinqContraband.dll");
        var linqContraband = new AnalyzerFileReference(competitor, GeneratorLoader.Instance).GetAnalyzers(LanguageNames.CSharp);
        var scores = new Dictionary<string, Score> { ["N+1 (NPlusOne)"] = new(), ["LinqContraband"] = new() };
        foreach (var folder in Corpus.Folders())
        {
            var options = Corpus.Options(folder);
            var excluded = options.TryGetValue("harness.exclude_references", out var list) ? list.Split(';') : [];
            var units = Units(Corpus.Sources(folder));
            var ours = await TestCompilation.AnalyzeProjectsAsync(Corpus.Projects(folder), options, excluded);
            var theirs = await TestCompilation.AnalyzeProjectsAsync(Corpus.Projects(folder), options, excluded, linqContraband);
            scores["N+1 (NPlusOne)"].Add(units, ours.Where(d => Growing.Contains(d.Id) || d.Id is "NP0004" or "NP0006"));
            scores["LinqContraband"].Add(units, theirs.Where(d => LinqContrabandRules.Contains(d.Id)));
        }

        var report = new StringBuilder();
        foreach (var (tool, score) in scores)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{tool}: found {score.Found} of {score.Found + score.Missed} proven N+1 methods; flagged {score.FalseAlarms} of {score.Quiet} proven-constant methods");
            foreach (var (id, (found, total)) in score.ByRule.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"    {id}: {found} of {total}");
            }

            foreach (var alarm in score.Alarms)
            {
                report.AppendLine("    flagged, proven constant: " + alarm);
            }
        }

        output.WriteLine(report.ToString());
    }

    // The methods and constructors of the sources, with what the ground truth proves about each.
    private static List<Unit> Units(IEnumerable<TestCompilation.Source> sources)
    {
        var units = new List<Unit>();
        foreach (var source in sources)
        {
            var text = Microsoft.CodeAnalysis.Text.SourceText.From(source.Text);
            var root = CSharpSyntaxTree.ParseText(text, TestCompilation.ParseOptions, source.Path).GetRoot();
            foreach (var member in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                var span = member.GetLocation().GetLineSpan();
                var ids = new HashSet<string>();
                var noted = false;
                for (var line = span.StartLinePosition.Line; line <= span.EndLinePosition.Line; line++)
                {
                    var lineText = text.Lines[line].ToString();
                    if (Marker().Match(lineText) is { Success: true } match)
                    {
                        ids.UnionWith(match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                    }

                    noted |= lineText.Contains("// ground-truth:", StringComparison.Ordinal);
                }

                var proven = noted ? Proven.Unknown
                    : ids.Count == 0 ? Proven.Constant
                    : ids.Overlaps(Growing) ? Proven.Grows
                    : Proven.Unknown;
                units.Add(new Unit(Path.GetFileName(source.Path), span.StartLinePosition.Line, span.EndLinePosition.Line, proven, [.. ids.Where(Growing.Contains)]));
            }
        }

        return units;
    }

    [GeneratedRegex(@"//\s*expect:\s*([A-Z0-9, ]+)\s*$")]
    private static partial Regex Marker();

    private enum Proven
    {
        Grows,
        Constant,
        Unknown,
    }

    private sealed record Unit(string File, int First, int Last, Proven Proven, ImmutableArray<string> Rules)
    {
        public bool Contains(Diagnostic diagnostic)
        {
            var span = diagnostic.Location.GetLineSpan();
            return Path.GetFileName(span.Path) == File && span.StartLinePosition.Line >= First && span.StartLinePosition.Line <= Last;
        }
    }

    private sealed class Score
    {
        public int Found { get; private set; }

        public int Missed { get; private set; }

        public int FalseAlarms { get; private set; }

        public int Quiet { get; private set; }

        public Dictionary<string, (int Found, int Total)> ByRule { get; } = [];

        public List<string> Alarms { get; } = [];

        public void Add(List<Unit> units, IEnumerable<Diagnostic> diagnostics)
        {
            var list = diagnostics.ToList();
            foreach (var unit in units)
            {
                // A nested method (a local function's container) is scored once, by its own lines only when it has none inside.
                var hit = list.Any(unit.Contains);
                switch (unit.Proven)
                {
                    case Proven.Grows:
                        if (hit)
                        {
                            Found++;
                        }
                        else
                        {
                            Missed++;
                        }

                        foreach (var rule in unit.Rules)
                        {
                            var (found, total) = ByRule.GetValueOrDefault(rule);
                            ByRule[rule] = (found + (hit ? 1 : 0), total + 1);
                        }

                        break;
                    case Proven.Constant:
                        Quiet++;
                        if (hit)
                        {
                            FalseAlarms++;
                            Alarms.AddRange(list.Where(unit.Contains).Select(d => $"{unit.File}:{d.Location.GetLineSpan().StartLinePosition.Line + 1} {d.Id} {d.GetMessage(CultureInfo.InvariantCulture)}"));
                        }

                        break;
                }
            }
        }
    }
}
