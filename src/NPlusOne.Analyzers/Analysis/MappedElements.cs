using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>Elements an object mapper maps one at a time: a mapped sequence, an element in a loop, or a <c>Map</c> method group.</summary>
/// <param name="Source">The elements' type.</param>
/// <param name="Origin">Where the elements come from.</param>
/// <param name="Destination">The destination element type.</param>
/// <param name="Library">The mapper library.</param>
/// <param name="Iteration">What makes the mapping run once per element.</param>
/// <param name="Location">Where to report.</param>
/// <param name="Key">The region and root lazy loads are deduplicated by, with the path.</param>
internal sealed record MappedElements(
    ITypeSymbol Source,
    SourceInfo Origin,
    ITypeSymbol Destination,
    MapperLibrary Library,
    Iteration Iteration,
    Location Location,
    (IOperation Region, ISymbol Root) Key);
