using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NPlusOne.Analyzers.Tests.Harness;

/// <summary>
/// Compiles C# sources against the assemblies the test process runs on (the shared framework,
/// ASP.NET Core, EF Core and its proxies) and runs the N+1 analyzer over them.
/// </summary>
public static class TestCompilation
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> LazyReferences = new(LoadReferences);

    /// <summary>The assemblies sources are compiled against.</summary>
    public static ImmutableArray<MetadataReference> References => LazyReferences.Value;

    /// <summary>
    /// C# latest, with the <c>NETn_0_OR_GREATER</c> symbols of the running framework defined, so a corpus case
    /// that needs a newer API than .NET 8 compiles (and runs) only where that API exists.
    /// </summary>
    public static CSharpParseOptions ParseOptions { get; } = new(
        LanguageVersion.Latest,
        preprocessorSymbols: [.. Enumerable.Range(5, Environment.Version.Major - 4).Select(v => $"NET{v}_0_OR_GREATER")]);

    /// <summary>The source files a compilation is made of: a path for messages and the text.</summary>
    public sealed record Source(string Path, string Text);

    /// <summary>One project of a multi-project compilation: its assembly name and sources.</summary>
    public sealed record Project(string Name, IReadOnlyList<Source> Sources);

    /// <summary>Compiles the sources and returns the analyzer's diagnostics, failing on compiler errors.</summary>
    public static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        IReadOnlyList<Source> sources,
        IReadOnlyDictionary<string, string>? options = null,
        IReadOnlyCollection<string>? excludedAssemblies = null)
        => AnalyzeProjectsAsync([new Project("Corpus", sources)], options, excludedAssemblies);

    /// <summary>
    /// Compiles projects in order, each referencing the ones before it the way a build does: the
    /// summary generator runs on each, its output is emitted, and the next project references the
    /// emitted assembly. Returns the analyzer's diagnostics of every project; with <paramref name="analyzers"/>, those
    /// analyzers' diagnostics instead (another tool run over the same corpus, to compare).
    /// </summary>
    public static async Task<ImmutableArray<Diagnostic>> AnalyzeProjectsAsync(
        IReadOnlyList<Project> projects,
        IReadOnlyDictionary<string, string>? options = null,
        IReadOnlyCollection<string>? excludedAssemblies = null,
        ImmutableArray<Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer>? analyzers = null)
    {
        var parseOptions = ParseOptions;
        var optionsProvider = new DictionaryOptionsProvider(options ?? new Dictionary<string, string>());
        var references = ReferencesWithout(excludedAssemblies);

        var all = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var project in projects)
        {
            var trees = project.Sources.Select(s => CSharpSyntaxTree.ParseText(s.Text, parseOptions, s.Path)).ToList();
            trees.Add(CSharpSyntaxTree.ParseText(GlobalUsings, parseOptions, project.Name + ".GlobalUsings.cs"));
            Compilation compilation = CSharpCompilation.Create(
                project.Name,
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));

            var driver = CSharpGeneratorDriver.Create([new NPlusOneSummaryGenerator().AsSourceGenerator(), .. LibraryGenerators.Value], optionsProvider: optionsProvider, parseOptions: parseOptions);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out compilation, out var generatorDiagnostics);

            var errors = compilation.GetDiagnostics().Concat(generatorDiagnostics).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"{project.Name} does not compile:\n" + string.Join("\n", errors.Take(20)));
            }

            var withAnalyzers = compilation.WithAnalyzers(analyzers ?? [new NPlusOneAnalyzer()], new AnalyzerOptions([], optionsProvider));
            var diagnostics = await withAnalyzers.GetAllDiagnosticsAsync();
            var crashes = diagnostics.Where(d => d.Id == "AD0001").ToList();
            if (crashes.Count > 0)
            {
                throw new InvalidOperationException("The analyzer threw:\n" + string.Join("\n", crashes));
            }

            all.AddRange(diagnostics.Where(d => analyzers is not null ? d.Location.IsInSource : d.Id.StartsWith("NP", StringComparison.Ordinal)));

            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            if (!emitted.Success)
            {
                throw new InvalidOperationException($"{project.Name} does not emit:\n" + string.Join("\n", emitted.Diagnostics.Take(20)));
            }

            references = references.Add(MetadataReference.CreateFromImage(image.ToArray(), filePath: project.Name + ".dll"));
        }

        return all.ToImmutable();
    }

    /// <summary>One compiled project: its assembly image and the syntax trees it was compiled from.</summary>
    public sealed record Emitted(string Name, byte[] Image, IReadOnlyList<SyntaxTree> Trees);

    /// <summary>
    /// Compiles projects in order, each referencing the ones before it, without running the analyzer,
    /// and returns their assembly images, so the corpus can be run as well as analysed.
    /// </summary>
    public static IReadOnlyList<Emitted> Emit(IReadOnlyList<Project> projects, IReadOnlyCollection<string>? excludedAssemblies = null)
    {
        var parseOptions = ParseOptions;
        var references = ReferencesWithout(excludedAssemblies);

        var emitted = new List<Emitted>();
        foreach (var project in projects)
        {
            var trees = project.Sources.Select(s => CSharpSyntaxTree.ParseText(s.Text, parseOptions, s.Path)).ToList();
            trees.Add(CSharpSyntaxTree.ParseText(GlobalUsings, parseOptions, project.Name + ".GlobalUsings.cs"));
            var compilation = CSharpCompilation.Create(
                project.Name,
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));

            CSharpGeneratorDriver.Create([.. LibraryGenerators.Value], parseOptions: parseOptions)
                .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out _);
            compilation = (CSharpCompilation)generated;

            using var image = new MemoryStream();
            var result = compilation.Emit(image);
            if (!result.Success)
            {
                throw new InvalidOperationException($"{project.Name} does not emit:\n" + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20)));
            }

            emitted.Add(new Emitted(project.Name, image.ToArray(), trees));
            references = references.Add(MetadataReference.CreateFromImage(image.ToArray(), filePath: project.Name + ".dll"));
        }

        return emitted;
    }

    /// <summary>The implicit usings of an SDK-style project, so sources read like real project files.</summary>
    public const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    /// <summary>
    /// Every assembly on the test host's trusted platform list: the runtime, ASP.NET Core, and the
    /// packages this project references (EF Core, its proxies).
    /// </summary>
    /// <summary>
    /// The source generators of the libraries the corpus uses (Mapperly), copied next to the tests: they run on every corpus
    /// project, as they would in a build that references the library.
    /// </summary>
    private static readonly Lazy<ImmutableArray<ISourceGenerator>> LibraryGenerators = new(() =>
    [
        .. Directory.GetFiles(System.IO.Path.Combine(AppContext.BaseDirectory, "generators"), "*.dll")
            .SelectMany(path => new Microsoft.CodeAnalysis.Diagnostics.AnalyzerFileReference(path, GeneratorLoader.Instance).GetGenerators(LanguageNames.CSharp)),
    ]);

    // The references without the excluded assemblies: a name, or a prefix ending in '*' (Volo.Abp.*).
    private static ImmutableArray<MetadataReference> ReferencesWithout(IReadOnlyCollection<string>? excluded)
    {
        if (excluded is not { Count: > 0 })
        {
            return References;
        }

        return [.. References.Where(r =>
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(r.Display!);
            return !excluded.Any(e => e.EndsWith('*') ? name.StartsWith(e[..^1], StringComparison.Ordinal) : name == e);
        })];
    }

    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(System.IO.Path.PathSeparator);
        return [.. paths.Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    }
}

/// <summary>Loads generator assemblies into the default load context, where the test's Roslyn already is.</summary>
internal sealed class GeneratorLoader : Microsoft.CodeAnalysis.IAnalyzerAssemblyLoader
{
    public static GeneratorLoader Instance { get; } = new();

    public void AddDependencyLocation(string fullPath)
    {
    }

    public System.Reflection.Assembly LoadFromPath(string fullPath) => System.Reflection.Assembly.LoadFrom(fullPath);
}
