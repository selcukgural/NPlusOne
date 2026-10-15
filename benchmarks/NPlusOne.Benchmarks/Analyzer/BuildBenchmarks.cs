using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NPlusOne.Analyzers;

namespace NPlusOne.Benchmarks.Analyzer;

/// <summary>
/// What the analyzer costs in a build: every document of a compilation object it has not seen. What it keeps across
/// compilations by declarations is still there, as in a compiler server that builds the same project again. A run takes
/// seconds, so it is held to two warmups and ten measured iterations.
/// </summary>
[InvocationCount(1)]
[WarmupCount(2)]
[IterationCount(10)]
public class BuildBenchmarks
{
    /// <summary>The generated application.</summary>
    private Compilation _compilation = null!;

    /// <summary>This iteration's compilation.</summary>
    private Compilation _build = null!;

    /// <summary>How many modules the application has; each is about 130 lines in one file.</summary>
    [Params(200, 1000)]
    public int Modules { get; set; }

    /// <summary>Generates and compiles the application.</summary>
    [GlobalSetup]
    public void Setup() => _compilation = GeneratedApplication.Compile(Modules);

    /// <summary>A new compilation object with the same sources.</summary>
    [IterationSetup]
    public void Clone() => _build = _compilation.Clone();

    /// <summary>Every document under the N+1 analyzer.</summary>
    /// <returns>The diagnostics, so the work is not optimised away.</returns>
    [Benchmark]
    public async Task<IReadOnlyList<Diagnostic>> WholeCompilation() =>
        await new CompilationWithAnalyzers(_build, [new NPlusOneAnalyzer()], GeneratedApplication.Options).GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);
}
