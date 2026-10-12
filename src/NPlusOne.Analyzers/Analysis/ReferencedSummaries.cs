using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using NPlusOne.Analyzers.Model;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// The summaries that referenced projects wrote into their assemblies with
/// <see cref="NPlusOneSummaryGenerator"/>: method facts by documentation id, the assemblies whose
/// summaries are complete (a method there without facts makes no round trip), and the navigations
/// their models auto-include.
/// </summary>
internal sealed class ReferencedSummaries
{
    /// <summary>The namespace of the generated attribute.</summary>
    public const string AttributeNamespace = "NPlusOne.Generated";

    /// <summary>The start of the generated attribute's name; each assembly adds its own suffix.</summary>
    public const string AttributePrefix = "NPlusOneSummary";

    /// <summary>
    /// The facts value of the marker that says an assembly's summaries are complete, with the format version. Change
    /// the version whenever the encoding in <see cref="SummaryExport"/> changes.
    /// </summary>
    public const string CompleteMarker = "complete:1";

    /// <summary>The facts value that marks an auto-included navigation property.</summary>
    public const string AutoIncludeMarker = "autoinclude";

    private readonly Dictionary<string, string> _facts;
    private readonly ImmutableHashSet<IAssemblySymbol> _complete;
    private readonly ImmutableArray<string> _autoIncludes;

    /// <summary>Creates the reader from what <see cref="Read"/> found.</summary>
    /// <param name="facts">Facts by assembly-qualified documentation id.</param>
    /// <param name="complete">The assemblies with complete summaries.</param>
    /// <param name="autoIncludes">Documentation ids of auto-included navigation properties.</param>
    private ReferencedSummaries(Dictionary<string, string> facts, ImmutableHashSet<IAssemblySymbol> complete, ImmutableArray<string> autoIncludes)
    {
        _facts = facts;
        _complete = complete;
        _autoIncludes = autoIncludes;
    }

    /// <summary>The assemblies whose summaries are complete.</summary>
    public ImmutableHashSet<IAssemblySymbol> CompleteAssemblies => _complete;

    /// <summary>Reads the summaries of every referenced assembly that has them.</summary>
    /// <param name="compilation">The compilation whose references are read.</param>
    public static ReferencedSummaries Read(Compilation compilation)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        var complete = ImmutableHashSet.CreateBuilder<IAssemblySymbol>(SymbolEqualityComparer.Default);
        var autoIncludes = ImmutableArray.CreateBuilder<string>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            var name = assembly.Identity.Name;
            if (EntityModel.IsFrameworkAssembly(name))
            {
                continue;
            }

            // Summaries written in another format version (an older or newer analyzer built the reference) are skipped
            // whole: the assembly then counts as unknown, which can miss a round trip but never invents one.
            var current = false;
            var ownFacts = new List<(string Member, string Value)>();
            foreach (var attribute in assembly.GetAttributes())
            {
                if (attribute.AttributeClass is not { } type
                    || !type.Name.StartsWith(AttributePrefix, StringComparison.Ordinal)
                    || type.ContainingNamespace?.ToDisplayString() != AttributeNamespace
                    || attribute.ConstructorArguments.Length != 2
                    || attribute.ConstructorArguments[0].Value is not string member
                    || attribute.ConstructorArguments[1].Value is not string value)
                {
                    continue;
                }

                if (member.Length == 0)
                {
                    current |= value == CompleteMarker;
                    continue;
                }

                ownFacts.Add((member, value));
            }

            if (!current)
            {
                continue;
            }

            complete.Add(assembly);
            foreach (var (member, value) in ownFacts)
            {
                if (value == AutoIncludeMarker)
                {
                    autoIncludes.Add(member);
                }
                else
                {
                    facts[Key(name, member)] = value;
                }
            }
        }

        return new ReferencedSummaries(facts, complete.ToImmutable(), autoIncludes.ToImmutable());
    }

    /// <summary>The facts written for a method of a referenced assembly, if any.</summary>
    /// <param name="method">The method (from metadata).</param>
    /// <param name="facts">The facts.</param>
    public bool TryGet(IMethodSymbol method, out string facts)
    {
        facts = "";
        return method.ContainingAssembly is { } assembly
               && DocumentationCommentId.CreateDeclarationId(method.OriginalDefinition) is { } id
               && _facts.TryGetValue(Key(assembly.Identity.Name, id), out facts!);
    }

    /// <summary>Whether a method's assembly wrote complete summaries, so the method has one (possibly empty).</summary>
    /// <param name="method">The method.</param>
    public bool Covers(IMethodSymbol method)
        => method.ContainingAssembly is { } assembly && (_complete.Contains(assembly) || TryGet(method, out _));

    /// <summary>The navigation properties referenced models auto-include.</summary>
    /// <param name="compilation">The compilation the ids are resolved in.</param>
    public ImmutableHashSet<IPropertySymbol> AutoIncludes(Compilation compilation)
    {
        var builder = ImmutableHashSet.CreateBuilder<IPropertySymbol>(SymbolEqualityComparer.Default);
        foreach (var id in _autoIncludes)
        {
            if (DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation) is IPropertySymbol property)
            {
                builder.Add(property.OriginalDefinition);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>The dictionary key: assembly name and documentation id.</summary>
    /// <param name="assembly">The assembly name.</param>
    /// <param name="id">The documentation id.</param>
    private static string Key(string assembly, string id) => assembly + "|" + id;
}
