using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace NPlusOne.Analyzers.Tests.Harness;

/// <summary>
/// Runs a code fix the way an IDE does: builds a workspace from the sources, takes the first
/// diagnostic with the given id, applies the fix's first action and returns the changed files.
/// </summary>
public static class CodeFixRunner
{
    /// <summary>The result of applying a fix: every file's text afterwards, and the analyzer's diagnostics on them.</summary>
    public sealed record Result(IReadOnlyDictionary<string, string> Files, ImmutableArray<Diagnostic> Diagnostics, bool Offered);

    /// <summary>Applies <paramref name="provider"/>'s first action for the first diagnostic with <paramref name="id"/>.</summary>
    public static async Task<Result> ApplyAsync(CodeFixProvider provider, string id, params (string Path, string Text)[] files)
    {
        var solution = CreateSolution(files, out var projectId);
        var diagnostics = await AnalyzeAsync(solution.GetProject(projectId)!);
        var diagnostic = diagnostics.First(d => d.Id == id);
        var document = solution.GetDocument(diagnostic.Location.SourceTree)!;

        var actions = new List<CodeAction>();
        await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
        if (actions.Count == 0)
        {
            return new Result(await TextsAsync(solution, projectId), diagnostics, Offered: false);
        }

        var operations = await actions[0].GetOperationsAsync(CancellationToken.None);
        var changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        var project = changed.GetProject(projectId)!;
        var errors = (await project.GetCompilationAsync())!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("The fixed code does not compile:\n" + string.Join("\n", errors));
        }

        return new Result(await TextsAsync(changed, projectId), await AnalyzeAsync(project), Offered: true);
    }

    /// <summary>A one-project solution with the sources and the test references.</summary>
    private static Solution CreateSolution((string Path, string Text)[] files, out ProjectId projectId)
    {
        var workspace = new AdhocWorkspace();
        projectId = ProjectId.CreateNewId();
        var info = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            "Fix",
            "Fix",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: TestCompilation.References);
        var solution = workspace.CurrentSolution.AddProject(info);
        foreach (var (path, text) in files.Append(("GlobalUsings.cs", TestCompilation.GlobalUsings)))
        {
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), path, SourceText.From(text), filePath: path);
        }

        return solution;
    }

    /// <summary>The analyzer's diagnostics for a project.</summary>
    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Project project)
    {
        var compilation = (await project.GetCompilationAsync())!;
        return await compilation.WithAnalyzers([new NPlusOneAnalyzer()]).GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>The text of every document, by path.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> TextsAsync(Solution solution, ProjectId projectId)
    {
        var texts = new Dictionary<string, string>();
        foreach (var document in solution.GetProject(projectId)!.Documents)
        {
            texts[document.FilePath!] = (await document.GetTextAsync()).ToString();
        }

        return texts;
    }
}
