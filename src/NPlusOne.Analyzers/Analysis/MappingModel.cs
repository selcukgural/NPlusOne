using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// A method a mapper calls once per mapped element with the source in one of its parameters: an AutoMapper value
/// resolver's <c>Resolve</c> or a type converter's <c>Convert</c>. What it reads on that parameter, and the queries it
/// runs, happen once per element.
/// </summary>
/// <param name="Method">The method.</param>
/// <param name="Ordinal">The parameter that receives the source (or the source member, for a member value resolver).</param>
/// <param name="Prefix">The navigation path from the mapped element to that parameter's value, empty for the element itself.</param>
internal sealed record MapperCallback(IMethodSymbol Method, int Ordinal, string Prefix);

/// <summary>A destination member's explicit configuration: the paths it reads and the callbacks that compute it.</summary>
/// <param name="Paths">Navigation paths read on the source.</param>
/// <param name="Callbacks">Resolvers that compute the member.</param>
internal sealed record MemberOverride(ImmutableArray<string> Paths, ImmutableArray<MapperCallback> Callbacks)
{
    /// <summary>A member mapped explicitly from nothing that reads a navigation (a <c>ForPath</c> root that is ignored).</summary>
    public static readonly MemberOverride Empty = new(ImmutableArray<string>.Empty, ImmutableArray<MapperCallback>.Empty);

    /// <summary>This override and another one for the same member (several <c>ForPath</c> calls under one root).</summary>
    /// <param name="other">The other override.</param>
    public MemberOverride Merge(MemberOverride other) => new(Paths.AddRange(other.Paths), Callbacks.AddRange(other.Callbacks));
}

/// <summary>What mapping one source element to a destination reads.</summary>
/// <param name="Paths">Navigation paths read on the element.</param>
/// <param name="Callbacks">Methods called with the element (resolvers, converters), whose reads and queries add to them.</param>
/// <param name="Configured">Whether a visible configuration decided them, rather than the naming conventions.</param>
internal sealed record MappingReads(ImmutableArray<string> Paths, ImmutableArray<MapperCallback> Callbacks, bool Configured);
