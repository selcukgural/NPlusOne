using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NPlusOne.Analyzers.CodeFixes;

/// <summary>
/// NP0003: moves a <c>SaveChanges</c> statement that sits directly in a loop body to right after
/// the loop, so the changes of all elements are saved in one round trip and one transaction.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MoveSaveChangesCodeFix))]
[Shared]
public sealed class MoveSaveChangesCodeFix : CodeFixProvider
{
    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("NP0003");

    /// <inheritdoc/>
    public override FixAllProvider? GetFixAllProvider() => null;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.ContainsKey(DiagnosticProperties.MovableSave) || root is null)
            {
                continue;
            }

            if (Movable(root.FindNode(diagnostic.Location.SourceSpan)) is not var (statement, loop))
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create("Call SaveChanges once after the loop", _ => Task.FromResult(Move(context.Document, root, statement, loop)), "NP0003.MoveAfterLoop"),
                diagnostic);
        }
    }

    /// <summary>
    /// The statement and loop when the call is a whole statement (<c>await db.SaveChangesAsync();</c>)
    /// directly in the body block of a loop that itself sits in a block; null otherwise, since moving
    /// anything else could change what the code does.
    /// </summary>
    /// <param name="node">The node of the diagnostic.</param>
    private static (ExpressionStatementSyntax Statement, StatementSyntax Loop)? Movable(SyntaxNode node)
    {
        var statement = node.FirstAncestorOrSelf<ExpressionStatementSyntax>();
        if (statement?.Parent is not BlockSyntax body
            || body.Parent is not (ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax)
            || body.Parent.Parent is not BlockSyntax)
        {
            return null;
        }

        return (statement, (StatementSyntax)body.Parent);
    }

    /// <summary>Removes the statement from the loop and inserts it after the loop.</summary>
    /// <param name="document">The document.</param>
    /// <param name="root">Its syntax root.</param>
    /// <param name="statement">The <c>SaveChanges</c> statement.</param>
    /// <param name="loop">The loop.</param>
    private static Document Move(Document document, SyntaxNode root, ExpressionStatementSyntax statement, StatementSyntax loop)
    {
        var tracked = root.TrackNodes(statement, loop);
        var withoutSave = tracked.RemoveNode(tracked.GetCurrentNode(statement)!, SyntaxRemoveOptions.KeepNoTrivia)!;
        var currentLoop = withoutSave.GetCurrentNode(loop)!;
        var moved = statement.WithLeadingTrivia(currentLoop.GetLeadingTrivia()).WithTrailingTrivia(statement.GetTrailingTrivia());
        return document.WithSyntaxRoot(withoutSave.InsertNodesAfter(currentLoop, [moved]));
    }
}
