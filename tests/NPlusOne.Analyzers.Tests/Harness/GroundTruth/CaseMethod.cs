using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NPlusOne.Analyzers.Tests.Harness.GroundTruth;

/// <summary>What running a corpus method must show, from the markers inside it.</summary>
public enum Expectation
{
    /// <summary>No marker: the method runs the same number of commands for any element count.</summary>
    Constant,

    /// <summary>A warning marker (NP0001, NP0002, NP0003, NP0005, NP0007): more elements, more commands.</summary>
    Grows,

    /// <summary>
    /// Only guesses or recursion markers (NP0004, NP0006), or a <c>// ground-truth: reason</c> comment saying the
    /// element count does not drive the method (it loops over services, or depends on which implementation is
    /// registered): measured and shown, not asserted.
    /// </summary>
    Unasserted,
}

/// <summary>
/// A method declared in a corpus file: the metadata name of its type, its name and parameter count (to
/// find it by reflection), the markers inside it, and whether an endpoint's result is serialized.
/// </summary>
/// <param name="TypeName">The metadata name of the declaring type.</param>
/// <param name="Name">The method name.</param>
/// <param name="ParameterCount">Its parameter count, to tell overloads apart.</param>
/// <param name="Location">File and line, for messages.</param>
/// <param name="Ids">The marker ids on its lines.</param>
/// <param name="IsMinimalApiHandler">Whether it is passed by name to a minimal API mapping.</param>
/// <param name="NotDrivenByData">The <c>// ground-truth:</c> reason, when the element count does not drive it.</param>
/// <param name="Route">
/// For a minimal API lambda (<c>app.MapGet("/orders", async (AppDb db) =&gt; ...)</c>), its route: the case runs the
/// mapping method on a real web application and calls that endpoint. Null for an ordinary method.
/// </param>
/// <param name="NewEntities">
/// Whether entity arguments are new, unsaved instances (<c>// ground-truth-arguments: new</c>, for a method that
/// adds what it is given) rather than entities the context loaded and tracks.
/// </param>
public sealed partial record CaseMethod(string TypeName, string Name, int ParameterCount, string Location, IReadOnlyList<string> Ids, bool IsMinimalApiHandler, string? NotDrivenByData, bool NewEntities, string? Route = null)
{
    private static readonly HashSet<string> Growing = ["NP0001", "NP0002", "NP0003", "NP0005", "NP0007"];

    public Expectation Expectation
        => NotDrivenByData is not null ? Expectation.Unasserted
            : Ids.Count == 0 ? Expectation.Constant
            : Ids.Any(Growing.Contains) ? Expectation.Grows
            : Expectation.Unasserted;

    public override string ToString() => $"{Location} {TypeName.Split('.', '+')[^1]}.{Name}" + (Route is null ? "" : $" {Route}");

    /// <summary>Every method with a body declared in the trees, with the marker ids on its lines.</summary>
    public static IReadOnlyList<CaseMethod> Collect(IEnumerable<SyntaxTree> trees)
    {
        var methods = new List<CaseMethod>();
        foreach (var tree in trees)
        {
            var text = tree.GetText();
            var handlers = MinimalApiHandler().Matches(text.ToString()).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (method.Body is null && method.ExpressionBody is null || method.Parent is not TypeDeclarationSyntax type)
                {
                    continue;
                }

                var span = method.GetLocation().GetLineSpan();
                var location = $"{Path.GetFileName(tree.FilePath)}({span.StartLinePosition.Line + 1})";
                var endpoints = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(IsLambdaEndpoint).ToList();

                // A method that maps lambdas is not a case itself: each endpoint is, with the markers on its lines.
                foreach (var endpoint in endpoints)
                {
                    var lines = endpoint.GetLocation().GetLineSpan();
                    var marks = Scan(text, lines.StartLinePosition.Line, lines.EndLinePosition.Line);
                    methods.Add(new CaseMethod(
                        MetadataName(type),
                        method.Identifier.ValueText,
                        method.ParameterList.Parameters.Count,
                        $"{Path.GetFileName(tree.FilePath)}({lines.StartLinePosition.Line + 1})",
                        marks.Ids,
                        false,
                        marks.Note,
                        marks.NewEntities,
                        ((LiteralExpressionSyntax)endpoint.ArgumentList.Arguments[0].Expression).Token.ValueText));
                }

                if (endpoints.Count > 0)
                {
                    continue;
                }

                var found = Scan(text, span.StartLinePosition.Line, span.EndLinePosition.Line);
                methods.Add(new CaseMethod(
                    MetadataName(type),
                    method.Identifier.ValueText,
                    method.ParameterList.Parameters.Count,
                    location,
                    found.Ids,
                    handlers.Contains(method.Identifier.ValueText),
                    found.Note,
                    found.NewEntities));
            }
        }

        return methods;
    }

    // The markers, the ground-truth note and the argument directive on a range of lines.
    private static (List<string> Ids, string? Note, bool NewEntities) Scan(Microsoft.CodeAnalysis.Text.SourceText text, int first, int last)
    {
        var ids = new List<string>();
        string? note = null;
        var newEntities = false;
        for (var line = first; line <= last; line++)
        {
            var lineText = text.Lines[line].ToString();
            if (Marker().Match(lineText) is { Success: true } match)
            {
                ids.AddRange(match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            // A method that runs code whose marker sits where the harness cannot run it (a validator's constructor).
            if (GroundTruthProves().Match(lineText) is { Success: true } proves)
            {
                ids.Add(proves.Groups[1].Value);
            }

            newEntities |= lineText.Contains("// ground-truth-arguments: new", StringComparison.Ordinal);
            if (GroundTruthNote().Match(lineText) is { Success: true } found)
            {
                note = found.Groups[1].Value.Trim();
            }
        }

        return (ids, note, newEntities);
    }

    // app.MapGet("/route", (...) => ...): a minimal API endpoint whose handler is a lambda.
    private static bool IsLambdaEndpoint(InvocationExpressionSyntax invocation)
        => invocation is { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch" }, ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax }, { Expression: AnonymousFunctionExpressionSyntax }] };

    // Namespace.Outer+Inner`1, as reflection names it.
    private static string MetadataName(TypeDeclarationSyntax type)
    {
        var name = type.Identifier.ValueText + (type.TypeParameterList is { } parameters ? "`" + parameters.Parameters.Count : "");
        return type.Parent switch
        {
            TypeDeclarationSyntax outer => MetadataName(outer) + "+" + name,
            BaseNamespaceDeclarationSyntax ns => Namespace(ns) + "." + name,
            _ => name,
        };
    }

    private static string Namespace(BaseNamespaceDeclarationSyntax ns)
        => ns.Parent is BaseNamespaceDeclarationSyntax outer ? Namespace(outer) + "." + ns.Name : ns.Name.ToString();

    [GeneratedRegex(@"//\s*expect:\s*([A-Z0-9, ]+)\s*$")]
    private static partial Regex Marker();

    [GeneratedRegex(@"//\s*ground-truth:\s*(.+)$")]
    private static partial Regex GroundTruthNote();

    [GeneratedRegex(@"//\s*ground-truth-proves:\s*(NP\d{4})")]
    private static partial Regex GroundTruthProves();

    [GeneratedRegex(@"\.Map(?:Get|Post|Put|Delete|Patch)\(\s*""[^""]*""\s*,\s*(\w+)\s*\)")]
    private static partial Regex MinimalApiHandler();
}
