using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Model;

/// <summary>
/// What the analysis learns about a compilation that depends only on its declarations and its references (the entity
/// types, the types implementing an interface, the mediator handlers), and what one unchanged file's code binds to (its
/// mapper configurations, its <c>AutoInclude</c> calls), kept by name and shared by every compilation with the same
/// declarations and references. Finding these resolves the base types of every type and binds whole files, work an IDE
/// would otherwise repeat on every keystroke; typing in a method body changes neither the declarations
/// (<see cref="SyntaxIndex.Declarations"/>) nor the references, and leaves the trees of the other files as they were.
/// </summary>
/// <param name="declarations">The hash of the compilation's declarations.</param>
/// <param name="references">The referenced assemblies, held weakly so the cache keeps no compilation alive.</param>
internal sealed class DeclarationFacts(long declarations, ImmutableArray<WeakReference<IAssemblySymbol>> references)
{
    /// <summary>The compilations remembered per assembly name: a project built for several target frameworks has one each.</summary>
    private const int EntriesPerAssembly = 4;

    /// <summary>The assembly names remembered; past this the cache starts over, so a long-lived compiler process stays small.</summary>
    private const int MaxAssemblies = 64;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<DeclarationFacts>> Remembered = new(StringComparer.Ordinal);

    /// <summary>The entities, once found and named, with the types the model configuration mapped when they were; null until then.</summary>
    private volatile NamedEntities? _entities;

    /// <summary>Whether an entity cannot be named (a type parameter), so the entities are collected for each compilation.</summary>
    private volatile bool _entitiesUnnamed;

    /// <summary>Serializes building <see cref="_interfaces"/>: it binds the base types of every candidate, once.</summary>
    private readonly object _interfacesGate = new();

    /// <summary>The interfaces of the candidate types of interface calls, once mapped.</summary>
    private InterfaceMap? _interfaces;

    /// <summary>Whether a candidate cannot be named, so implementers are found for each compilation.</summary>
    private bool _interfacesUnnamed;

    /// <summary>The answers of <see cref="ForTree{T}"/>, by tree and question; released with the tree.</summary>
    private readonly ConditionalWeakTable<SyntaxTree, ConcurrentDictionary<string, object?>> _trees = new();

    /// <summary>The facts of a compilation: those shared with an earlier one that has the same declarations and references, or new ones.</summary>
    /// <param name="compilation">The compilation.</param>
    public static DeclarationFacts For(Compilation compilation)
    {
        var assemblyName = compilation.AssemblyName ?? string.Empty;
        var declarations = DeclarationsOf(compilation);
        var references = compilation.SourceModule.ReferencedAssemblySymbols;
        lock (Gate)
        {
            if (Remembered.TryGetValue(assemblyName, out var entries))
            {
                foreach (var entry in entries)
                {
                    if (entry.Matches(declarations, references))
                    {
                        return entry;
                    }
                }
            }
            else
            {
                if (Remembered.Count >= MaxAssemblies)
                {
                    Remembered.Clear();
                }

                entries = [];
                Remembered[assemblyName] = entries;
            }

            var weak = ImmutableArray.CreateBuilder<WeakReference<IAssemblySymbol>>(references.Length);
            foreach (var reference in references)
            {
                weak.Add(new WeakReference<IAssemblySymbol>(reference));
            }

            var facts = new DeclarationFacts(declarations, weak.MoveToImmutable());
            if (entries.Count >= EntriesPerAssembly)
            {
                entries.RemoveAt(0);
            }

            entries.Add(facts);
            return facts;
        }
    }

    /// <summary>
    /// The entity types of a compilation these facts are for: found by name when known, else collected and named. The
    /// types the model configuration maps (<c>modelBuilder.Entity&lt;T&gt;()</c>) are read from method bodies, which the
    /// declarations leave out, so the names are reused only for the same mapped types.
    /// </summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="configured">The types the model configuration maps, by name; null when one cannot be named, and then nothing is reused.</param>
    /// <param name="collect">Finds the entities.</param>
    public ImmutableHashSet<ITypeSymbol> Entities(Compilation compilation, ImmutableArray<TypeName>? configured, Func<ImmutableHashSet<ITypeSymbol>> collect)
    {
        var known = _entities;
        if (known is not null && configured is { } mapped && known.Configured.SequenceEqual(mapped)
            && TypeName.Resolve(compilation, known.Entities) is { } types)
        {
            return ImmutableHashSet.CreateRange<ITypeSymbol>(SymbolEqualityComparer.Default, types);
        }

        var entities = collect();
        if (!_entitiesUnnamed && configured is { } named)
        {
            if (TypeName.Of(compilation, entities) is { } names)
            {
                _entities = new NamedEntities(named, names);
            }
            else
            {
                _entitiesUnnamed = true;
            }
        }

        return entities;
    }

    /// <summary>
    /// Of the candidate types, those implementing an interface (directly, through a base type or another interface), in
    /// candidate order. The interfaces of every candidate are mapped once, by name, for the compilations with these
    /// declarations; each compilation then resolves only the implementers it asks for.
    /// </summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="candidates">The types an interface call can land in, the same for every compilation with these declarations.</param>
    /// <param name="iface">The interface definition.</param>
    public ImmutableArray<INamedTypeSymbol> Implementers(Compilation compilation, ImmutableArray<INamedTypeSymbol> candidates, INamedTypeSymbol iface)
    {
        if (InterfacesOf(compilation, candidates) is not { } map || TypeName.Of(compilation, iface) is not { } name)
        {
            return Implementing(candidates, implemented => SymbolEqualityComparer.Default.Equals(implemented, iface));
        }

        return map.Implementers.TryGetValue(name, out var positions)
            ? map.Resolve(compilation, positions) ?? Implementing(candidates, implemented => SymbolEqualityComparer.Default.Equals(implemented, iface))
            : ImmutableArray<INamedTypeSymbol>.Empty;
    }

    /// <summary>
    /// Of the candidate types, those implementing one of the interfaces of a namespace with the given names (the mediator
    /// handlers), in candidate order; mapped once like <see cref="Implementers"/>.
    /// </summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="candidates">The types to look at, the same for every compilation with these declarations.</param>
    /// <param name="ns">The interfaces' namespace.</param>
    /// <param name="names">The interfaces' names, without arity.</param>
    public ImmutableArray<INamedTypeSymbol> ImplementersOfAny(Compilation compilation, ImmutableArray<INamedTypeSymbol> candidates, string ns, ImmutableHashSet<string> names)
    {
        bool Wanted(INamedTypeSymbol implemented) => names.Contains(implemented.Name) && implemented.ContainingNamespace?.ToDisplayString() == ns;
        if (InterfacesOf(compilation, candidates) is not { } map)
        {
            return Implementing(candidates, Wanted);
        }

        var positions = new SortedSet<int>();
        foreach (var entry in map.Implementers)
        {
            if (IsIn(entry.Key, ns, names))
            {
                positions.UnionWith(entry.Value);
            }
        }

        return map.Resolve(compilation, positions) ?? Implementing(candidates, Wanted);
    }

    /// <summary>Whether a metadata name (<c>MediatR.IRequestHandler`2</c>) is of a namespace and one of the names.</summary>
    /// <param name="name">The interface name.</param>
    /// <param name="ns">The namespace.</param>
    /// <param name="names">The names, without arity.</param>
    private static bool IsIn(TypeName name, string ns, ImmutableHashSet<string> names)
    {
        var full = name.MetadataName;
        if (full.Length <= ns.Length || full[ns.Length] != '.' || !full.StartsWith(ns, StringComparison.Ordinal) || full.IndexOf('+') >= 0)
        {
            return false;
        }

        var simple = full.Substring(ns.Length + 1);
        var tick = simple.IndexOf('`');
        return simple.IndexOf('.') < 0 && names.Contains(tick < 0 ? simple : simple.Substring(0, tick));
    }

    /// <summary>The candidates implementing an interface the test accepts, found without the map.</summary>
    /// <param name="candidates">The candidates.</param>
    /// <param name="accept">Accepts an interface definition.</param>
    private static ImmutableArray<INamedTypeSymbol> Implementing(ImmutableArray<INamedTypeSymbol> candidates, Func<INamedTypeSymbol, bool> accept)
    {
        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        foreach (var type in candidates)
        {
            foreach (var implemented in type.AllInterfaces)
            {
                if (accept(implemented.OriginalDefinition))
                {
                    builder.Add(type);
                    break;
                }
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>The interface map, built on first use by one thread while the others wait; null when a candidate cannot be named.</summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="candidates">The candidates.</param>
    private InterfaceMap? InterfacesOf(Compilation compilation, ImmutableArray<INamedTypeSymbol> candidates)
    {
        lock (_interfacesGate)
        {
            if (_interfaces is null && !_interfacesUnnamed)
            {
                _interfaces = InterfaceMap.Build(compilation, candidates);
                _interfacesUnnamed = _interfaces is null;
            }

            return _interfaces;
        }
    }

    /// <summary>The entities by name, and the types the model configuration mapped when they were found.</summary>
    /// <param name="Configured">The types <c>modelBuilder.Entity</c> calls mapped, in the order the files were read.</param>
    /// <param name="Entities">The entities.</param>
    private sealed record NamedEntities(ImmutableArray<TypeName> Configured, ImmutableArray<TypeName> Entities);

    /// <summary>The candidates of an interface call by name, and for each interface the positions of the candidates implementing it.</summary>
    /// <param name="Types">The candidates, in order.</param>
    /// <param name="Implementers">For each interface definition, the positions of its implementers among the candidates, ascending.</param>
    private sealed record InterfaceMap(ImmutableArray<TypeName> Types, Dictionary<TypeName, List<int>> Implementers)
    {
        /// <summary>Maps the interfaces of the candidates; null when one cannot be named.</summary>
        /// <param name="compilation">The compilation.</param>
        /// <param name="candidates">The candidates.</param>
        public static InterfaceMap? Build(Compilation compilation, ImmutableArray<INamedTypeSymbol> candidates)
        {
            if (TypeName.Of(compilation, candidates) is not { } types)
            {
                return null;
            }

            var implementers = new Dictionary<TypeName, List<int>>();
            for (var i = 0; i < candidates.Length; i++)
            {
                foreach (var implemented in candidates[i].AllInterfaces)
                {
                    if (TypeName.Of(compilation, implemented) is not { } name)
                    {
                        continue;
                    }

                    if (!implementers.TryGetValue(name, out var positions))
                    {
                        positions = [];
                        implementers[name] = positions;
                    }

                    if (positions.Count == 0 || positions[positions.Count - 1] != i)
                    {
                        positions.Add(i);
                    }
                }
            }

            return new InterfaceMap(types, implementers);
        }

        /// <summary>The candidates at the positions, in order; null when one is not found.</summary>
        /// <param name="compilation">The compilation.</param>
        /// <param name="positions">The positions, ascending.</param>
        public ImmutableArray<INamedTypeSymbol>? Resolve(Compilation compilation, IEnumerable<int> positions)
        {
            var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
            foreach (var position in positions)
            {
                if (Types[position].Resolve(compilation) is not { } type)
                {
                    return null;
                }

                builder.Add(type);
            }

            return builder.ToImmutable();
        }
    }

    /// <summary>
    /// The answer to a question about the code of one tree, asked once for the compilations with these declarations
    /// that contain this very tree: binding it gives the same result in each of them. The answer must hold no symbols
    /// or operations (names and positions), so the cache keeps no compilation alive; null when it cannot be told that
    /// way, and then the caller binds the tree each time.
    /// </summary>
    /// <typeparam name="T">The answer's type.</typeparam>
    /// <param name="tree">The tree.</param>
    /// <param name="question">Names the question.</param>
    /// <param name="answer">Binds the tree and answers.</param>
    public T? ForTree<T>(SyntaxTree tree, string question, Func<T?> answer)
        where T : class
    {
        var answers = _trees.GetValue(tree, static _ => new ConcurrentDictionary<string, object?>(StringComparer.Ordinal));
        return (T?)answers.GetOrAdd(question, _ => answer());
    }

    /// <summary>The declarations of every tree of the compilation, hashed in order.</summary>
    /// <param name="compilation">The compilation.</param>
    private static long DeclarationsOf(Compilation compilation)
    {
        var hash = unchecked((long)14695981039346656037UL);
        foreach (var tree in compilation.SyntaxTrees)
        {
            hash = unchecked((hash ^ SyntaxIndex.Of(tree).Declarations) * 1099511628211L);
        }

        return hash;
    }

    /// <summary>Whether a compilation has the same declarations and the very same referenced assemblies.</summary>
    /// <param name="otherDeclarations">Its declarations hash.</param>
    /// <param name="otherReferences">Its referenced assemblies.</param>
    private bool Matches(long otherDeclarations, ImmutableArray<IAssemblySymbol> otherReferences)
    {
        if (otherDeclarations != declarations || otherReferences.Length != references.Length)
        {
            return false;
        }

        for (var i = 0; i < references.Length; i++)
        {
            if (!references[i].TryGetTarget(out var reference) || !ReferenceEquals(reference, otherReferences[i]))
            {
                return false;
            }
        }

        return true;
    }
}
