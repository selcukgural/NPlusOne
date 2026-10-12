using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Model;

/// <summary>A navigation property of an entity.</summary>
/// <param name="Property">The property (its original definition).</param>
/// <param name="Target">The entity type it leads to (the element type of a collection).</param>
/// <param name="IsCollection">Whether it holds many entities.</param>
/// <param name="IsLazy">Whether reading it loads it when the query did not.</param>
/// <param name="IsAutoIncluded">Whether the model loads it with every query (<c>AutoInclude()</c>) unless the query ignores auto-includes.</param>
/// <param name="IsLazyWhenUntracked">
/// Whether reading it on an entity of a no-tracking query loads it too. From EF Core 8 on a lazy navigation
/// of an untracked entity loads when the values the query needs are on the entity: its key (for a
/// collection, or a reference whose foreign key is on the other side) or a foreign key property of its
/// own. A reference whose foreign key is a shadow property cannot load and throws instead; before EF
/// Core 8 nothing loads for untracked entities.
/// </param>
internal sealed record Navigation(IPropertySymbol Property, ITypeSymbol Target, bool IsCollection, bool IsLazy, bool IsAutoIncluded, bool IsLazyWhenUntracked);
