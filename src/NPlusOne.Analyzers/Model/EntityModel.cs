using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace NPlusOne.Analyzers.Model;

/// <summary>
/// Which types are entities and which of their properties are navigations that load lazily.
/// Entities are the <c>DbSet&lt;T&gt;</c> types of every context the compilation can see, the types
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> classes configure, the types reachable from those
/// through navigations that look like entities (they have a key), and the types deriving from an entity
/// (a hierarchy with a <c>DbSet</c> for its base type only).
/// </summary>
/// <param name="compilation">The compilation being analysed.</param>
/// <param name="symbols">Its known symbols.</param>
/// <param name="mode">The lazy loading setting.</param>
/// <param name="referencedAutoIncludes">The navigations referenced projects' models auto-include, read on first use.</param>
internal sealed class EntityModel(Compilation compilation, KnownSymbols symbols, LazyLoadingMode mode, Func<ImmutableHashSet<IPropertySymbol>> referencedAutoIncludes)
{
    /// <summary>Assemblies whose types are never entities, skipped to keep the scan cheap.</summary>
    private static readonly string[] FrameworkPrefixes = ["System", "Microsoft", "mscorlib", "netstandard", "Newtonsoft", "xunit", "NUnit", "Castle"];

    private readonly KnownSymbols _symbols = symbols;
    private readonly ImmutableHashSet<ITypeSymbol> _entities = CollectEntities(compilation, symbols);

    /// <summary>Whether proxies can load navigations: forced by the setting, or the proxies package is referenced.</summary>
    private readonly bool _lazyLoadingAvailable = mode switch
    {
        LazyLoadingMode.Enabled => true,
        LazyLoadingMode.Disabled => false,
        _ => symbols.ProxiesExtensions is not null,
    };

    private readonly ConcurrentDictionary<ITypeSymbol, ImmutableArray<Navigation>> _navigations = new(SymbolEqualityComparer.Default);

    /// <summary>Whether an entity takes a lazy loader: 0 not looked yet, 1 none does, 2 one does.</summary>
    private int _lazyLoaderEntity;

    /// <summary>The navigations the model configures with <c>AutoInclude()</c>, found on first use.</summary>
    private readonly Lazy<ImmutableHashSet<IPropertySymbol>> _autoIncluded = new(() => CollectAutoIncludes(compilation));
    private readonly Lazy<ImmutableHashSet<IPropertySymbol>> _referencedAutoIncluded = new(referencedAutoIncludes);

    /// <summary>The navigations this compilation's own model configuration auto-includes, which the summary generator exports.</summary>
    public ImmutableHashSet<IPropertySymbol> OwnAutoIncludes => _autoIncluded.Value;

    /// <summary>The lazy loading setting the model was built with.</summary>
    public LazyLoadingMode Mode { get; } = mode;

    /// <summary>Whether any navigation of any entity can load lazily; when not, NP0002 and NP0005 never fire.</summary>
    public bool AnyLazyLoading => Mode != LazyLoadingMode.Disabled && (_lazyLoadingAvailable || HasLazyLoaderEntity());

    /// <summary>Whether <paramref name="type"/> is an entity type.</summary>
    /// <param name="type">The type to test.</param>
    public bool IsEntity(ITypeSymbol? type) => type is not null && _entities.Contains(type.OriginalDefinition);

    /// <summary>The navigation <paramref name="property"/> represents, or null when it is not one.</summary>
    /// <param name="property">A property read in the code.</param>
    public Navigation? AsNavigation(IPropertySymbol property)
    {
        if (!IsEntity(property.ContainingType))
        {
            return null;
        }

        foreach (var navigation in NavigationsOf(property.ContainingType))
        {
            if (SymbolEqualityComparer.Default.Equals(navigation.Property, property.OriginalDefinition))
            {
                return navigation;
            }
        }

        return null;
    }

    /// <summary>The navigations of an entity type, lazy or not.</summary>
    /// <param name="entity">The entity type.</param>
    public ImmutableArray<Navigation> NavigationsOf(ITypeSymbol entity)
        => _navigations.GetOrAdd(entity.OriginalDefinition, BuildNavigations);

    /// <summary>Finds the entity types of a compilation.</summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="symbols">Its known symbols.</param>
    private static ImmutableHashSet<ITypeSymbol> CollectEntities(Compilation compilation, KnownSymbols symbols)
    {
        var roots = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var candidates = CandidateTypes(compilation).ToList();
        foreach (var type in candidates)
        {
            if (KnownSymbols.InheritsFrom(type, symbols.DbContext) && symbols.DbSet is not null)
            {
                foreach (var member in type.GetMembers())
                {
                    if (member is IPropertySymbol { Type: INamedTypeSymbol propertyType }
                        && SymbolEqualityComparer.Default.Equals(propertyType.OriginalDefinition, symbols.DbSet))
                    {
                        roots.Add(propertyType.TypeArguments[0].OriginalDefinition);
                    }
                }
            }

            if (symbols.EntityTypeConfiguration is null)
            {
                continue;
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, symbols.EntityTypeConfiguration))
                {
                    roots.Add(iface.TypeArguments[0].OriginalDefinition);
                }
            }
        }

        // Close over navigations (Customer.Address makes Address an entity when it has a key) and derived types
        // (Company : Party makes Company an entity, and its own navigations count), until nothing is added.
        var entities = new HashSet<ITypeSymbol>(roots, SymbolEqualityComparer.Default);
        var pending = new Stack<ITypeSymbol>(roots);
        while (pending.Count > 0)
        {
            CloseOverNavigations(entities, pending, symbols);
            foreach (var type in candidates)
            {
                if (type.TypeKind == TypeKind.Class && !entities.Contains(type.OriginalDefinition) && DerivesFromEntity(type, entities))
                {
                    entities.Add(type.OriginalDefinition);
                    pending.Push(type.OriginalDefinition);
                }
            }
        }

        return ImmutableHashSet.CreateRange<ITypeSymbol>(SymbolEqualityComparer.Default, entities);
    }

    /// <summary>Whether a type's base types include an entity.</summary>
    /// <param name="type">The type.</param>
    /// <param name="entities">The entities found so far.</param>
    private static bool DerivesFromEntity(INamedTypeSymbol type, HashSet<ITypeSymbol> entities)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (entities.Contains(current.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Adds the entity types the pending entities' navigations lead to, until none is pending.</summary>
    /// <param name="entities">The entities found so far.</param>
    /// <param name="pending">The entities whose navigations are not followed yet.</param>
    /// <param name="symbols">The known symbols.</param>
    private static void CloseOverNavigations(HashSet<ITypeSymbol> entities, Stack<ITypeSymbol> pending, KnownSymbols symbols)
    {
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                var target = symbols.ElementType(property.Type) ?? property.Type;
                if (target is not INamedTypeSymbol { TypeKind: TypeKind.Class } named || entities.Contains(named.OriginalDefinition))
                {
                    continue;
                }

                if (IsFrameworkType(named) || !HasKey(named))
                {
                    continue;
                }

                entities.Add(named.OriginalDefinition);
                pending.Push(named.OriginalDefinition);
            }
        }
    }

    /// <summary>The source types and the types of referenced non-framework assemblies.</summary>
    /// <param name="compilation">The compilation.</param>
    private static IEnumerable<INamedTypeSymbol> CandidateTypes(Compilation compilation)
    {
        foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace))
        {
            yield return type;
        }

        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (IsFrameworkAssembly(assembly.Name))
            {
                continue;
            }

            foreach (var type in AllTypes(assembly.GlobalNamespace))
            {
                yield return type;
            }
        }
    }

    /// <summary>Every type in a namespace tree, nested types included.</summary>
    /// <param name="ns">The root namespace.</param>
    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(ns);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var member in current.GetMembers())
            {
                if (member is INamespaceSymbol childNamespace)
                {
                    stack.Push(childNamespace);
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    stack.Push(type);
                }
            }
        }
    }

    /// <summary>Whether a type has a key by convention (<c>Id</c>, <c>{Type}Id</c>) or by <c>[Key]</c>.</summary>
    /// <param name="type">A candidate entity type.</param>
    private static bool HasKey(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.Name == "Id" || property.Name == type.Name + "Id")
                {
                    return true;
                }

                foreach (var attribute in property.GetAttributes())
                {
                    if (attribute.AttributeClass?.Name == "KeyAttribute")
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Whether a type comes from the framework or a well-known library.</summary>
    /// <param name="type">The type.</param>
    private static bool IsFrameworkType(INamedTypeSymbol type)
        => type.ContainingAssembly is null || IsFrameworkAssembly(type.ContainingAssembly.Name);

    /// <summary>
    /// Whether an assembly name belongs to the framework or a well-known library: the name itself or one of its
    /// dotted children (<c>System.Runtime</c>), never an application that merely starts the same way (<c>SystemMonitor.Data</c>).
    /// </summary>
    /// <param name="name">The assembly name.</param>
    internal static bool IsFrameworkAssembly(string name)
    {
        foreach (var prefix in FrameworkPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && (name.Length == prefix.Length || name[prefix.Length] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lists the navigations of one entity type, including inherited ones.</summary>
    /// <param name="entity">The entity type.</param>
    private ImmutableArray<Navigation> BuildNavigations(ITypeSymbol entity)
    {
        var loaderMembers = TakesLazyLoader(entity) && Mode != LazyLoadingMode.Disabled ? LazyLoaderMembers(entity) : null;
        var builder = ImmutableArray.CreateBuilder<Navigation>();
        for (var current = entity; current is not null; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.GetMethod is null)
                {
                    continue;
                }

                var element = _symbols.ElementType(property.Type);
                var target = element ?? property.Type;
                if (!IsEntity(target))
                {
                    continue;
                }

                var lazy = (_lazyLoadingAvailable && property.IsVirtual && !property.IsSealed) || (loaderMembers is not null && AsksLoader(property, loaderMembers));
                var autoIncluded = _autoIncluded.Value.Contains(property.OriginalDefinition) || _referencedAutoIncluded.Value.Contains(property.OriginalDefinition);
                var lazyWhenUntracked = lazy && _symbols.EfCoreMajorVersion >= 8 && (element is not null || HasLoadableForeignKey(entity, property, target));
                builder.Add(new Navigation(property.OriginalDefinition, target.OriginalDefinition, element is not null, lazy, autoIncluded, lazyWhenUntracked));
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Whether an untracked entity holds what loading a reference navigation needs, by EF Core's naming
    /// conventions: a foreign key property of its own (<c>CustomerId</c> for <c>Customer</c>), or a foreign key
    /// on the target pointing back (then the entity's key is enough). Without either the foreign key is a
    /// shadow property, and EF Core throws instead of loading.
    /// </summary>
    /// <param name="entity">The entity declaring the navigation.</param>
    /// <param name="navigation">The reference navigation.</param>
    /// <param name="target">The entity it leads to.</param>
    private static bool HasLoadableForeignKey(ITypeSymbol entity, IPropertySymbol navigation, ITypeSymbol target)
    {
        if (HasProperty(entity, navigation.Name + "Id") || HasProperty(entity, target.Name + "Id"))
        {
            return true;
        }

        foreach (var back in target.GetMembers().OfType<IPropertySymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(back.Type.OriginalDefinition, entity.OriginalDefinition)
                && (HasProperty(target, back.Name + "Id") || HasProperty(target, entity.Name + "Id")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a type or one of its base types declares a property of that name.</summary>
    /// <param name="type">The type.</param>
    /// <param name="name">The property name.</param>
    private static bool HasProperty(ITypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetMembers(name).OfType<IPropertySymbol>().Any())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds <c>builder.Navigation(x =&gt; x.Prop).AutoInclude()</c> calls in the compilation's model
    /// configuration (<c>OnModelCreating</c>, <c>IEntityTypeConfiguration</c>). Only files that mention
    /// <c>AutoInclude</c> get a semantic model, so the scan costs nothing in projects that do not use it.
    /// </summary>
    /// <param name="compilation">The compilation.</param>
    private static ImmutableHashSet<IPropertySymbol> CollectAutoIncludes(Compilation compilation)
    {
        var builder = ImmutableHashSet.CreateBuilder<IPropertySymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!tree.GetText().ToString().Contains("AutoInclude"))
            {
                continue;
            }

#pragma warning disable RS1030 // Model configuration lives in other files than the code being analysed; this runs once per compilation.
            var model = compilation.GetSemanticModel(tree);
#pragma warning restore RS1030
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AutoInclude" } }
                    && model.GetOperation(node) is IInvocationOperation invocation
                    && !DisablesAutoInclude(invocation)
                    && invocation.Instance is IInvocationOperation { TargetMethod.Name: "Navigation" } navigation
                    && navigation.Arguments.Length > 0
                    && navigation.Arguments[0].Value is var argument
                    && FindProperty(argument) is { } property)
                {
                    builder.Add(property.OriginalDefinition);
                }
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>Whether the call is <c>AutoInclude(false)</c>.</summary>
    /// <param name="invocation">The <c>AutoInclude</c> call.</param>
    private static bool DisablesAutoInclude(IInvocationOperation invocation)
        => invocation.Arguments.Length > 0 && invocation.Arguments[0].Value.ConstantValue is { HasValue: true, Value: false };

    /// <summary>The property a navigation lambda (<c>x =&gt; x.Customer</c>) selects.</summary>
    /// <param name="argument">The lambda argument.</param>
    private static IPropertySymbol? FindProperty(IOperation argument)
    {
        foreach (var descendant in argument.DescendantsAndSelf())
        {
            if (descendant is IPropertyReferenceOperation reference)
            {
                return reference.Property;
            }
        }

        return null;
    }

    /// <summary>Whether any entity takes a lazy loader (no proxies needed). Asked on every block, so the answer is kept.</summary>
    private bool HasLazyLoaderEntity()
    {
        var known = Volatile.Read(ref _lazyLoaderEntity);
        if (known != 0)
        {
            return known == 2;
        }

        var found = false;
        foreach (var entity in _entities)
        {
            if (TakesLazyLoader(entity))
            {
                found = true;
                break;
            }
        }

        Volatile.Write(ref _lazyLoaderEntity, found ? 2 : 1);
        return found;
    }

    /// <summary>
    /// The names of the fields and properties through which an entity reaches its lazy loader (an
    /// <c>ILazyLoader</c> or the <c>Action&lt;object, string&gt;</c> delegate), inherited ones included.
    /// </summary>
    /// <param name="entity">An entity that takes a lazy loader.</param>
    private HashSet<string> LazyLoaderMembers(ITypeSymbol entity)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var current = entity; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                var type = member switch
                {
                    IFieldSymbol field => field.Type,
                    IPropertySymbol property => property.Type,
                    _ => null,
                };
                if (type is not null && IsLazyLoaderType(type))
                {
                    names.Add(member.Name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Whether reading a navigation of a lazy-loader entity asks the loader: its getter mentions one of
    /// the loader members (<c>get =&gt; LazyLoader.Load(this, ref _posts)</c>). A plain auto-property is
    /// never loaded lazily; it stays null until included. A property without source (in a referenced
    /// assembly) cannot be inspected and counts as lazy.
    /// </summary>
    /// <param name="property">The navigation property.</param>
    /// <param name="loaderMembers">The names of the entity's loader fields and properties.</param>
    private static bool AsksLoader(IPropertySymbol property, HashSet<string> loaderMembers)
    {
        var references = property.DeclaringSyntaxReferences;
        if (references.Length == 0)
        {
            return true;
        }

        foreach (var reference in references)
        {
            if (reference.GetSyntax() is not PropertyDeclarationSyntax declaration)
            {
                return true;
            }

            SyntaxNode? getter = declaration.ExpressionBody;
            if (getter is null && declaration.AccessorList is { } accessors)
            {
                getter = accessors.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) is { } get
                    ? (SyntaxNode?)get.Body ?? get.ExpressionBody
                    : null;
            }

            if (getter is not null && getter.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => loaderMembers.Contains(i.Identifier.ValueText)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a type is a lazy loader: <c>ILazyLoader</c> or <c>Action&lt;object, string&gt;</c>.</summary>
    /// <param name="type">The type.</param>
    private bool IsLazyLoaderType(ITypeSymbol type)
        => (_symbols.LazyLoader is not null && SymbolEqualityComparer.Default.Equals(type, _symbols.LazyLoader))
           || type is INamedTypeSymbol { Name: "Action", TypeArguments.Length: 2 } action
           && action.TypeArguments[0].SpecialType == SpecialType.System_Object
           && action.TypeArguments[1].SpecialType == SpecialType.System_String;

    /// <summary>
    /// Whether an entity loads lazily without proxies: it receives an <c>ILazyLoader</c> or the
    /// <c>Action&lt;object, string&gt;</c> lazy-loader delegate in a constructor.
    /// </summary>
    /// <param name="entity">The entity type.</param>
    private bool TakesLazyLoader(ITypeSymbol entity)
    {
        if (entity is not INamedTypeSymbol named)
        {
            return false;
        }

        foreach (var constructor in named.InstanceConstructors)
        {
            foreach (var parameter in constructor.Parameters)
            {
                if (IsLazyLoaderType(parameter.Type))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
