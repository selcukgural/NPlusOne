using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NPlusOne.Analyzers.Tests.Harness;

namespace NPlusOne.Benchmarks.Analyzer;

/// <summary>
/// The application the analyzer benchmarks analyse: the analyzer tests' generated shop (Harness/LargeSolution.cs), many
/// modules of entities, services with N+1 and clean methods, handlers, controllers and mapper profiles, compiled against
/// the assemblies this process loads.
/// </summary>
internal static class GeneratedApplication
{
    /// <summary>The analyzer options: none, as in a project without an <c>.editorconfig</c> entry.</summary>
    public static AnalyzerOptions Options { get; } = new([], new DictionaryOptionsProvider(new Dictionary<string, string>()));

    /// <summary>Generates and compiles the application.</summary>
    /// <param name="modules">How many modules; each is about 130 lines in one file.</param>
    /// <returns>The compilation.</returns>
    /// <exception cref="InvalidOperationException">The generated sources do not compile.</exception>
    public static Compilation Compile(int modules)
    {
        List<SyntaxTree> trees = [.. LargeSolution.Sources(modules).Select(static s => CSharpSyntaxTree.ParseText(s.Text, TestCompilation.ParseOptions, s.Path))];
        trees.Add(CSharpSyntaxTree.ParseText(TestCompilation.GlobalUsings, TestCompilation.ParseOptions, "GlobalUsings.cs"));
        Compilation compilation = CSharpCompilation.Create(
            "Shop",
            trees,
            TestCompilation.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));

        var errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).Take(20).ToList();
        return errors.Count == 0 ? compilation : throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }
}
