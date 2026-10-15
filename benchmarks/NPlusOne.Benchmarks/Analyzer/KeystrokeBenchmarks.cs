using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NPlusOne.Analyzers;
using NPlusOne.Analyzers.Tests.Harness;

namespace NPlusOne.Benchmarks.Analyzer;

/// <summary>
/// What the analyzer costs in an IDE: every keystroke makes a new compilation and the IDE analyses the open document in
/// it. Each iteration makes one edit to a module of the generated application, then analyses that document.
/// <see cref="Subscribing"/> only subscribes to the same operations: the cost of binding the document, which the IDE pays
/// anyway, and the floor of any operation analyzer.
/// </summary>
/// <remarks>
/// A body edit changes a literal inside a method, so every declaration stays the same; a declaration edit adds a member,
/// as typing a new method does, and changes what the analyzer keeps across compilations by declarations.
/// </remarks>
[InvocationCount(1)]
[IterationCount(15)]
public class KeystrokeBenchmarks
{
    /// <summary>The member a declaration edit adds in front of, in every module's service.</summary>
    private const string MemberAnchor = "public decimal Sum(Order order)";

    /// <summary>The generated application, before any edit.</summary>
    private Compilation _compilation = null!;

    /// <summary>How many edits have been made, which picks the module and the text of the next one.</summary>
    private int _keystrokes;

    /// <summary>The compilation after this iteration's edit.</summary>
    private Compilation _edited = null!;

    /// <summary>The document this iteration's edit changed.</summary>
    private SyntaxTree _document = null!;

    /// <summary>How many modules the application has; each is about 130 lines in one file.</summary>
    [Params(200, 1000)]
    public int Modules { get; set; }

    /// <summary>What the keystroke changes.</summary>
    [Params(EditKind.Body, EditKind.Declaration)]
    public EditKind Edit { get; set; }

    /// <summary>Generates and compiles the application.</summary>
    [GlobalSetup]
    public void Setup() => _compilation = GeneratedApplication.Compile(Modules);

    /// <summary>Makes the next edit in a new compilation, as an editor does.</summary>
    [IterationSetup]
    public void Type()
    {
        var keystroke = _keystrokes++;
        var digits = keystroke.ToString(CultureInfo.InvariantCulture);
        var path = LargeSolution.ModulePath(keystroke * 37 % Modules);
        var tree = _compilation.SyntaxTrees.Single(t => t.FilePath == path);
        var text = tree.GetText();
        var source = text.ToString();
        TextChange change;
        if (Edit == EditKind.Body)
        {
            var start = source.IndexOf(LargeSolution.EditMarker, StringComparison.Ordinal) + LargeSolution.EditMarker.Length;
            change = new TextChange(TextSpan.FromBounds(start, source.IndexOf(';', start)), digits);
        }
        else
        {
            var anchor = source.IndexOf(MemberAnchor, StringComparison.Ordinal);
            change = new TextChange(new TextSpan(anchor, 0), $"public int Typed{digits}() => {digits};\n\n            ");
        }

        _document = tree.WithChangedText(text.WithChanges(change));
        _edited = _compilation.ReplaceSyntaxTree(tree, _document);
    }

    /// <summary>The edited document under an analyzer that only subscribes: the floor.</summary>
    /// <returns>The diagnostics, so the work is not optimised away.</returns>
    [Benchmark(Baseline = true)]
    public Task<IReadOnlyList<Diagnostic>> Subscribing() => AnalyzeDocument(new SubscribingAnalyzer());

    /// <summary>The edited document under the N+1 analyzer.</summary>
    /// <returns>The diagnostics.</returns>
    [Benchmark]
    public Task<IReadOnlyList<Diagnostic>> NPlusOne() => AnalyzeDocument(new NPlusOneAnalyzer());

    /// <summary>Analyses the edited document in the edited compilation, as the IDE does after a keystroke.</summary>
    /// <param name="analyzer">The analyzer.</param>
    /// <returns>The diagnostics.</returns>
    private async Task<IReadOnlyList<Diagnostic>> AnalyzeDocument(DiagnosticAnalyzer analyzer)
    {
        var withAnalyzers = new CompilationWithAnalyzers(_edited, [analyzer], GeneratedApplication.Options);
        var model = withAnalyzers.Compilation.GetSemanticModel(_document);
        return await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, null, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>What a keystroke changes.</summary>
    public enum EditKind
    {
        /// <summary>A literal inside a method body.</summary>
        Body,

        /// <summary>A new member of a class.</summary>
        Declaration,
    }
}
