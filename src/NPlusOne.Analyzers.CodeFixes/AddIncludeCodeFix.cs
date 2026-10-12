using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NPlusOne.Analyzers.CodeFixes;

/// <summary>
/// NP0002: adds <c>.Include(x =&gt; x.A).ThenInclude(x =&gt; x.B)</c> right after the root of the query that
/// produced the elements (<c>db.Orders</c>), which may be in another file, and the
/// <c>Microsoft.EntityFrameworkCore</c> using it needs.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddIncludeCodeFix))]
[Shared]
public sealed class AddIncludeCodeFix : CodeFixProvider
{
    /// <summary>The namespace of EF Core's <c>Include</c> and <c>ThenInclude</c>.</summary>
    private const string EfNamespace = "Microsoft.EntityFrameworkCore";

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("NP0002");

    /// <summary>
    /// No fix-all: two includes for the same query would both rewrite its root, and the batch fixer
    /// cannot merge edits that replace the same span.
    /// </summary>
    public override FixAllProvider? GetFixAllProvider() => null;

    /// <inheritdoc/>
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(DiagnosticProperties.IncludePath, out var path)
                || string.IsNullOrEmpty(path)
                || diagnostic.AdditionalLocations.Count == 0)
            {
                continue;
            }

            var root = diagnostic.AdditionalLocations[0];
            var call = IncludeCall(path!);
            context.RegisterCodeFix(
                CodeAction.Create($"Add {call} to the query", token => AddIncludeAsync(context.Document.Project.Solution, root, path!, token), "NP0002.AddInclude." + path),
                diagnostic);
        }

        return Task.CompletedTask;
    }

    /// <summary>Rewrites the query root in its document.</summary>
    /// <param name="solution">The solution.</param>
    /// <param name="root">The location of the query's root expression.</param>
    /// <param name="path">The navigation path to include.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private static async Task<Solution> AddIncludeAsync(Solution solution, Location root, string path, CancellationToken cancellationToken)
    {
        if (root.SourceTree is null || solution.GetDocument(root.SourceTree) is not { } document)
        {
            return solution;
        }

        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (syntaxRoot?.FindNode(root.SourceSpan, getInnermostNodeForTie: true) is not ExpressionSyntax query)
        {
            return solution;
        }

        var rewritten = SyntaxFactory.ParseExpression(query.WithoutTrivia().ToString() + IncludeCall(path)).WithTriviaFrom(query);
        var newRoot = syntaxRoot.ReplaceNode(query, rewritten);
        if (newRoot is CompilationUnitSyntax unit && !HasEfUsing(unit))
        {
            newRoot = unit.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(EfNamespace)).NormalizeWhitespace().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed));
        }

        return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
    }

    /// <summary>Whether the file already imports EF Core's namespace (or a namespace inside it is declared there).</summary>
    /// <param name="unit">The compilation unit.</param>
    private static bool HasEfUsing(CompilationUnitSyntax unit)
    {
        foreach (var directive in unit.Usings)
        {
            if (directive.Name?.ToString() == EfNamespace)
            {
                return true;
            }
        }

        foreach (var member in unit.Members)
        {
            if (member is BaseNamespaceDeclarationSyntax ns)
            {
                foreach (var directive in ns.Usings)
                {
                    if (directive.Name?.ToString() == EfNamespace)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>The include call for a path: <c>Lines.Product</c> gives <c>.Include(x =&gt; x.Lines).ThenInclude(x =&gt; x.Product)</c>.</summary>
    /// <param name="path">The navigation path.</param>
    private static string IncludeCall(string path)
    {
        var segments = path.Split('.');
        var text = "";
        for (var i = 0; i < segments.Length; i++)
        {
            text += (i == 0 ? ".Include(x => x." : ".ThenInclude(x => x.") + segments[i] + ")";
        }

        return text;
    }
}
