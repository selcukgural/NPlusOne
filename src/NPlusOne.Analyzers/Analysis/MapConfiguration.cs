using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// One visible map (AutoMapper's <c>CreateMap&lt;TSource, TDestination&gt;()</c>, Mapster's <c>NewConfig</c>) with what
/// is chained on it.
/// </summary>
internal sealed class MapConfiguration
{
    /// <summary>
    /// Destination members configured explicitly (<c>ForMember</c>, <c>ForPath</c>, Mapster's <c>Map</c>/<c>Ignore</c>),
    /// by name: null for an ignored member, what computes it otherwise. The conventions fill only the other members.
    /// </summary>
    public Dictionary<string, MemberOverride?> Members { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The source members of <c>IncludeMembers(s =&gt; s.Customer)</c>, with their types: a destination member the
    /// source itself cannot fill is filled from them by the same conventions.
    /// </summary>
    public List<(string Path, ITypeSymbol Type)> IncludedMembers { get; } = [];

    /// <summary>
    /// The conversion that replaces the whole map (<c>ConvertUsing</c>), or null: then no member is mapped by
    /// convention, and only what the converter reads counts.
    /// </summary>
    public MemberOverride? Converter { get; set; }
}
