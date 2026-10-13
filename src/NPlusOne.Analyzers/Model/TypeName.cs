using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Model;

/// <summary>
/// A type definition by name, valid across compilations with the very same referenced assemblies: the position of its
/// assembly among them and its metadata name. Keeps answers about one compilation for the next without keeping its symbols.
/// </summary>
/// <param name="Reference">The position of its assembly among the referenced assemblies, or their count for the compilation's own.</param>
/// <param name="MetadataName">The namespace-qualified metadata name (<c>Shop.Order</c>, <c>Shop.Outer+Inner</c>, <c>Shop.Entity`1</c>).</param>
internal sealed record TypeName(int Reference, string MetadataName)
{
    /// <summary>The name of a type definition, or null for a type parameter or a type of no referenced assembly.</summary>
    /// <param name="compilation">The compilation the type is seen from.</param>
    /// <param name="type">The type.</param>
    public static TypeName? Of(Compilation compilation, ITypeSymbol type)
    {
        if (type.OriginalDefinition is not INamedTypeSymbol { ContainingAssembly: { } assembly } named)
        {
            return null;
        }

        var references = compilation.SourceModule.ReferencedAssemblySymbols;
        var reference = SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly)
            ? references.Length
            : references.IndexOf(assembly, 0, SymbolEqualityComparer.Default);
        if (reference < 0)
        {
            return null;
        }

        var builder = new StringBuilder(named.MetadataName);
        for (var outer = named.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            builder.Insert(0, outer.MetadataName + "+");
        }

        for (var ns = named.ContainingNamespace; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
        {
            builder.Insert(0, ns.MetadataName + ".");
        }

        return new TypeName(reference, builder.ToString());
    }

    /// <summary>The names of types, or null when one cannot be named.</summary>
    /// <param name="compilation">The compilation the types are seen from.</param>
    /// <param name="types">The types.</param>
    public static ImmutableArray<TypeName>? Of(Compilation compilation, IEnumerable<ITypeSymbol> types)
    {
        var names = ImmutableArray.CreateBuilder<TypeName>();
        foreach (var type in types)
        {
            if (Of(compilation, type) is not { } name)
            {
                return null;
            }

            names.Add(name);
        }

        return names.ToImmutable();
    }

    /// <summary>The types of names, or null when one is not found.</summary>
    /// <param name="compilation">A compilation with the same referenced assemblies as the one the names were taken from.</param>
    /// <param name="names">The names.</param>
    public static ImmutableArray<INamedTypeSymbol>? Resolve(Compilation compilation, ImmutableArray<TypeName> names)
    {
        var types = ImmutableArray.CreateBuilder<INamedTypeSymbol>(names.Length);
        foreach (var name in names)
        {
            if (name.Resolve(compilation) is not { } type)
            {
                return null;
            }

            types.Add(type);
        }

        return types.MoveToImmutable();
    }

    /// <summary>The type of this name, or null when it is not found.</summary>
    /// <param name="compilation">A compilation with the same referenced assemblies as the one the name was taken from.</param>
    public INamedTypeSymbol? Resolve(Compilation compilation)
    {
        var references = compilation.SourceModule.ReferencedAssemblySymbols;
        var assembly = Reference == references.Length ? compilation.Assembly : Reference < references.Length ? references[Reference] : null;
        return assembly?.GetTypeByMetadataName(MetadataName);
    }
}
