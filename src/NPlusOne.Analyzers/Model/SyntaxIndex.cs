using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NPlusOne.Analyzers.Model;

/// <summary>
/// What the whole-compilation scans need from one syntax tree, found once and kept with the tree: the nodes they bind
/// (registrations such as <c>AddNewtonsoftJson</c>, pipeline behaviors and minimal API mappings, mapper configurations,
/// <c>AutoInclude</c> and <c>Entity</c> calls) and a hash of its declarations. An IDE makes a new compilation on every keystroke but keeps
/// the trees of the files that did not change, so after an edit only the edited file is walked again, and each scan binds
/// a handful of nodes instead of reading every file.
/// </summary>
/// <param name="calls">The calls to the names the scans look for.</param>
/// <param name="mappings">The member calls whose name starts with <c>Map</c>, when one of them maps an endpoint.</param>
/// <param name="behaviorTypes">The <c>typeof</c> expressions and <c>AddBehavior</c>/<c>AddOpenBehavior</c> names, when the file names a pipeline behavior.</param>
/// <param name="declarations">The hash of the tree's declarations.</param>
internal sealed class SyntaxIndex(
    ImmutableArray<InvocationExpressionSyntax> calls,
    ImmutableArray<InvocationExpressionSyntax> mappings,
    ImmutableArray<SyntaxNode> behaviorTypes,
    long declarations)
{
    /// <summary>The index of each tree seen, released with the tree.</summary>
    private static readonly ConditionalWeakTable<SyntaxTree, SyntaxIndex> Indexes = new();

    /// <summary>The names of the calls the scans bind, besides the <c>Map*</c> endpoint mappings.</summary>
    private static readonly ImmutableHashSet<string> CallNames = ImmutableHashSet.Create(
        StringComparer.Ordinal, "AddNewtonsoftJson", "AutoInclude", "CreateMap", "NewConfig", "ForType", "Entity");

    /// <summary>The minimal API mappings that make a file's <c>Map*</c> calls worth binding.</summary>
    private static readonly ImmutableHashSet<string> EndpointMappings = ImmutableHashSet.Create(
        StringComparer.Ordinal, "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods");

    /// <summary>The calls to <c>AddNewtonsoftJson</c>, <c>AutoInclude</c>, <c>CreateMap</c>, <c>NewConfig</c>, <c>ForType</c> and <c>Entity</c>.</summary>
    public ImmutableArray<InvocationExpressionSyntax> Calls { get; } = calls;

    /// <summary>
    /// The member calls whose name starts with <c>Map</c> (<c>app.MapGet(...)</c>, <c>group.MapPost(...)</c>), kept only
    /// when one of them is a minimal API mapping.
    /// </summary>
    public ImmutableArray<InvocationExpressionSyntax> Mappings { get; } = mappings;

    /// <summary>
    /// The <c>typeof(...)</c> expressions and the generic <c>AddBehavior</c>/<c>AddOpenBehavior</c> names, kept only when the
    /// file names a pipeline behavior (<c>IPipelineBehavior</c>, <c>AddBehavior</c>, <c>AddOpenBehavior</c>), where
    /// behaviors are registered.
    /// </summary>
    public ImmutableArray<SyntaxNode> BehaviorTypes { get; } = behaviorTypes;

    /// <summary>
    /// A hash of the tree's declarations: every token outside bodies and the initializers of properties and fields
    /// (usings, types, base lists, attributes, member signatures, constants). Typing in a method body keeps it, so what
    /// depends only on declarations can be reused from the previous compilation.
    /// </summary>
    public long Declarations { get; } = declarations;

    /// <summary>The index of a tree, built on first use.</summary>
    /// <param name="tree">The tree.</param>
    public static SyntaxIndex Of(SyntaxTree tree) => Indexes.GetValue(tree, Build);

    /// <summary>The calls of a tree to one of the scanned names.</summary>
    /// <param name="tree">The tree.</param>
    /// <param name="name">The name called.</param>
    public static IEnumerable<InvocationExpressionSyntax> CallsTo(SyntaxTree tree, string name)
    {
        foreach (var call in Of(tree).Calls)
        {
            if (CalledName(call) == name)
            {
                yield return call;
            }
        }
    }

    /// <summary>The name a call invokes: <c>Name</c> in <c>Name(...)</c>, <c>x.Name(...)</c>, <c>x?.Name(...)</c> and their generic forms.</summary>
    /// <param name="call">The call.</param>
    public static string? CalledName(InvocationExpressionSyntax call) => call.Expression switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>Walks a tree once for every node the scans need, and once more over its declarations.</summary>
    /// <param name="tree">The tree.</param>
    private static SyntaxIndex Build(SyntaxTree tree)
    {
        var calls = ImmutableArray.CreateBuilder<InvocationExpressionSyntax>();
        var mappings = ImmutableArray.CreateBuilder<InvocationExpressionSyntax>();
        var behaviorTypes = ImmutableArray.CreateBuilder<SyntaxNode>();
        var mapsEndpoints = false;
        var namesBehavior = false;
        var root = tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax call when CalledName(call) is { } name:
                    if (CallNames.Contains(name))
                    {
                        calls.Add(call);
                    }

                    if (call.Expression is MemberAccessExpressionSyntax && name.StartsWith("Map", StringComparison.Ordinal))
                    {
                        mappings.Add(call);
                        mapsEndpoints |= EndpointMappings.Contains(name);
                    }

                    break;
                case TypeOfExpressionSyntax typeOf:
                    behaviorTypes.Add(typeOf);
                    break;
                case SimpleNameSyntax name when NamesBehavior(name.Identifier.ValueText):
                    namesBehavior = true;
                    if (name is GenericNameSyntax { Identifier.ValueText: "AddBehavior" or "AddOpenBehavior" })
                    {
                        behaviorTypes.Add(name);
                    }

                    break;
            }
        }

        return new SyntaxIndex(
            calls.ToImmutable(),
            mapsEndpoints ? mappings.ToImmutable() : ImmutableArray<InvocationExpressionSyntax>.Empty,
            namesBehavior ? behaviorTypes.ToImmutable() : ImmutableArray<SyntaxNode>.Empty,
            HashDeclarations(root));
    }

    /// <summary>Whether a name mentions a pipeline behavior or its registration.</summary>
    /// <param name="name">An identifier.</param>
    private static bool NamesBehavior(string name)
        => name.Contains("PipelineBehavior") || name.Contains("AddBehavior") || name.Contains("AddOpenBehavior");

    /// <summary>
    /// Hashes the kind and text of every token outside bodies (blocks, expression bodies, top-level statements) and the
    /// initializers of properties and fields (FNV-1a). Constants stay in, since code elsewhere binds to their values.
    /// Trivia (whitespace, comments) is left out, so reformatting keeps the hash too.
    /// </summary>
    /// <param name="root">The tree's root.</param>
    private static long HashDeclarations(SyntaxNode root)
    {
        var hash = unchecked((long)14695981039346656037UL);
        foreach (var token in root.DescendantTokens(static node => node switch
                 {
                     BlockSyntax or ArrowExpressionClauseSyntax or GlobalStatementSyntax => false,
                     EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax } => false,
                     EqualsValueClauseSyntax { Parent.Parent.Parent: FieldDeclarationSyntax field } => field.Modifiers.Any(SyntaxKind.ConstKeyword),
                     _ => true,
                 }))
        {
            hash = Mix(hash, token.RawKind);
            foreach (var character in token.ValueText)
            {
                hash = Mix(hash, character);
            }
        }

        return hash;
    }

    /// <summary>One FNV-1a step.</summary>
    /// <param name="hash">The hash so far.</param>
    /// <param name="value">The value to add.</param>
    private static long Mix(long hash, int value) => unchecked((hash ^ value) * 1099511628211L);
}
