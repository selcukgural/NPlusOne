using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NPlusOne.Analyzers.Tests.Harness;
using Xunit.Abstractions;

namespace NPlusOne.Analyzers.Tests;

// Measures what the analyzer costs in an IDE: every keystroke makes a new compilation, and the IDE analyses the open
// document in it. Prints the time and allocations of one document's analysis after an edit, next to an analyzer that only
// subscribes to the same operations (the cost of binding the document, which the IDE pays anyway):
// NPLUSONE_BENCH=2000 dotnet test -f net10.0 -c Release --filter PerformanceTests --logger "console;verbosity=detailed"
// (the number is the modules generated; NPLUSONE_BENCH_KEYSTROKES=100 keeps it typing long enough to attach a profiler).
public class PerformanceTests(ITestOutputHelper output)
{
    private const int Keystrokes = 12;

    [Fact]
    public async Task Measures_a_keystroke_in_a_large_compilation()
    {
        if (Environment.GetEnvironmentVariable("NPLUSONE_BENCH") is not { } value)
        {
            return;
        }

        var modules = int.TryParse(value, out var count) && count > 1 ? count : 1000;
        var trees = LargeSolution.Sources(modules).Select(s => CSharpSyntaxTree.ParseText(s.Text, TestCompilation.ParseOptions, s.Path)).ToList();
        trees.Add(CSharpSyntaxTree.ParseText(TestCompilation.GlobalUsings, TestCompilation.ParseOptions, "GlobalUsings.cs"));
        Compilation compilation = CSharpCompilation.Create(
            "Shop",
            trees,
            TestCompilation.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(20).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));

        var options = new AnalyzerOptions([], new DictionaryOptionsProvider(new Dictionary<string, string>()));
        DiagnosticAnalyzer[] analyzers = [new SubscribingAnalyzer(), new NPlusOneAnalyzer()];
        var lines = trees.Sum(t => t.GetText().Lines.Count);
        output.WriteLine($"{modules} modules, {trees.Count} files, {lines} lines");

        foreach (var analyzer in analyzers)
        {
            var watch = Stopwatch.StartNew();
            var diagnostics = await new CompilationWithAnalyzers(compilation, [analyzer], options).GetAnalyzerDiagnosticsAsync();
            output.WriteLine($"{analyzer.GetType().Name}: whole compilation {watch.ElapsedMilliseconds} ms, {diagnostics.Length} diagnostics");
        }

        var keystrokes = int.TryParse(Environment.GetEnvironmentVariable("NPLUSONE_BENCH_KEYSTROKES"), out var asked) && asked > 2 ? asked : Keystrokes;
        var samples = analyzers.ToDictionary(a => a, _ => new List<(double Ms, long Bytes)>());
        for (var keystroke = 0; keystroke < keystrokes; keystroke++)
        {
            (compilation, var edited) = Type(compilation, LargeSolution.ModulePath(keystroke * 37 % modules), keystroke);
            foreach (var analyzer in analyzers)
            {
                var withAnalyzers = new CompilationWithAnalyzers(compilation, [analyzer], options);
                var model = withAnalyzers.Compilation.GetSemanticModel(edited);
                var before = GC.GetTotalAllocatedBytes(true);
                var watch = Stopwatch.StartNew();
                await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, null, CancellationToken.None);
                samples[analyzer].Add((watch.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - before));
            }
        }

        // The first keystrokes JIT the analyzer's paths; the median of the rest is the steady state.
        foreach (var (analyzer, runs) in samples)
        {
            var steady = runs.Skip(2).ToList();
            output.WriteLine(
                $"{analyzer.GetType().Name}: keystroke median {Median(steady.Select(r => r.Ms)):F1} ms, " +
                $"{Median(steady.Select(r => (double)r.Bytes)) / 1024 / 1024:F1} MB allocated (first {runs[0].Ms:F0} ms)");
        }
    }

    /// <summary>Changes the literal a module's service starts its count with, as typing a digit would.</summary>
    private static (Compilation Compilation, SyntaxTree Edited) Type(Compilation compilation, string path, int keystroke)
    {
        var tree = compilation.SyntaxTrees.Single(t => t.FilePath == path);
        var text = tree.GetText();
        var start = text.ToString().IndexOf(LargeSolution.EditMarker, StringComparison.Ordinal) + LargeSolution.EditMarker.Length;
        var end = text.ToString().IndexOf(';', start);
        var edited = tree.WithChangedText(text.WithChanges(new TextChange(TextSpan.FromBounds(start, end), keystroke.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        return (compilation.ReplaceSyntaxTree(tree, edited), edited);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    // A measuring stick, never shipped: the rules for analyzer projects and released diagnostics do not apply.
#pragma warning disable RS1036, RS2008

    /// <summary>Subscribes to the operations the N+1 analyzer subscribes to and does nothing: the floor of any operation analyzer.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class SubscribingAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Unused = new("BENCH", "Unused", "Unused", "Bench", DiagnosticSeverity.Hidden, true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Unused];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start => start.RegisterOperationBlockStartAction(block =>
            {
                block.RegisterOperationAction(static _ => { }, OperationKind.Invocation, OperationKind.Loop, OperationKind.PropertyReference, OperationKind.Return, OperationKind.YieldReturn, OperationKind.Interpolation, OperationKind.Conversion, OperationKind.ObjectCreation);
                block.RegisterOperationBlockEndAction(static _ => { });
            }));
        }
    }
}
