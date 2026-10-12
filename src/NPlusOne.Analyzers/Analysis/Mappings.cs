using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using NPlusOne.Analyzers.Model;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>The object mapper libraries whose calls and configuration are recognised.</summary>
internal enum MapperLibrary
{
    /// <summary>AutoMapper: <c>IMapper.Map</c>, configured with <c>CreateMap</c>.</summary>
    AutoMapper,

    /// <summary>Mapster: <c>Adapt</c> and <c>MapsterMapper.IMapper.Map</c>, configured with <c>TypeAdapterConfig</c>.</summary>
    Mapster,
}

/// <summary>A call to an object mapper: the value it maps and the type it maps to.</summary>
/// <param name="Source">The mapped value.</param>
/// <param name="Destination">The destination type (a collection type when a collection is mapped).</param>
/// <param name="Library">The library, whose own configuration decides the mapping.</param>
internal sealed record MapperCall(IOperation Source, ITypeSymbol Destination, MapperLibrary Library);

/// <summary>
/// Which lazy navigations an object mapper reads on each source element. AutoMapper's
/// <c>CreateMap&lt;TSource, TDestination&gt;()</c> calls in the compilation, with their
/// <c>ForMember(..., o =&gt; o.MapFrom(...))</c> and <c>Ignore()</c> overrides, value resolvers, <c>ForPath</c>,
/// <c>IncludeMembers</c> and <c>ConvertUsing</c>, and Mapster's
/// <c>TypeAdapterConfig&lt;TSource, TDestination&gt;.NewConfig()</c> with its <c>Map</c> and <c>Ignore</c> calls make
/// the answer exact; without a visible configuration the naming conventions are applied: a destination
/// member named like a navigation maps it, and one named <c>CustomerName</c> flattens <c>Customer.Name</c>.
/// </summary>
/// <param name="compilation">The compilation.</param>
/// <param name="entities">Its entity model.</param>
/// <param name="symbols">Its known symbols.</param>
internal sealed class Mappings(Compilation compilation, EntityModel entities, KnownSymbols symbols)
{
    /// <summary>How deep nested destination objects are followed.</summary>
    private const int MaxDepth = 2;

    private readonly Lazy<Dictionary<(MapperLibrary Library, ITypeSymbol Source, ITypeSymbol Destination), MapConfiguration>> _maps = new(() => CollectMaps(compilation));
    private readonly ConcurrentDictionary<(MapperLibrary, ITypeSymbol, ITypeSymbol), MappingReads> _reads = new();

    /// <summary>
    /// Recognises <c>IMapper.Map</c> (AutoMapper's, and Mapster's in <c>MapsterMapper</c>) and Mapster's
    /// <c>Adapt</c>. The destination is the last type argument (<c>Map&lt;TDestination&gt;</c>,
    /// <c>Map&lt;TSource, TDestination&gt;</c>), or the type of the destination object passed in.
    /// </summary>
    /// <param name="invocation">The call.</param>
    public static MapperCall? AsMapperCall(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (invocation.Arguments.Length == 0 || LibraryOf(method) is not { } library)
        {
            return null;
        }

        var destination = method.TypeArguments.Length > 0
            ? method.TypeArguments[method.TypeArguments.Length - 1]
            : invocation.Arguments.Length > 1 ? Operations.StripConversions(invocation.Arguments[1].Value)?.Type : null;
        return destination is null || destination.SpecialType == SpecialType.System_Object || destination.ToDisplayString() == "System.Type"
            ? null
            : new MapperCall(invocation.Arguments[0].Value, destination, library);
    }

    /// <summary>The library a mapping method belongs to: <c>IMapper.Map</c> (AutoMapper's or Mapster's) or Mapster's <c>Adapt</c>.</summary>
    /// <param name="method">The method called, or passed as a method group.</param>
    public static MapperLibrary? LibraryOf(IMethodSymbol method) => (method.ContainingNamespace?.ToDisplayString(), method.Name) switch
    {
        ("AutoMapper", "Map") => MapperLibrary.AutoMapper,
        ("MapsterMapper", "Map") or ("Mapster", "Adapt") => MapperLibrary.Mapster,
        _ => null,
    };

    /// <summary>
    /// The navigation paths (relative to <paramref name="source"/>) mapping one element to
    /// <paramref name="destination"/> reads, the resolvers it calls with the element, and whether a visible
    /// configuration decided them.
    /// </summary>
    /// <param name="source">The source element type.</param>
    /// <param name="destination">The destination element type.</param>
    /// <param name="library">The mapper library, whose configuration is consulted.</param>
    public MappingReads Reads(ITypeSymbol source, ITypeSymbol destination, MapperLibrary library)
    {
        var key = (library, source.OriginalDefinition, destination.OriginalDefinition);
        return _reads.GetOrAdd(key, k => _maps.Value.TryGetValue(k, out var map)
            ? Configured(source, destination, map)
            : new MappingReads(Convention(source, destination, MaxDepth), ImmutableArray<MapperCallback>.Empty, false));
    }

    /// <summary>
    /// Applies a visible map: a converter replaces it whole; otherwise explicit members first, conventions for the
    /// other members, on the source itself and then on the members it includes (<c>IncludeMembers</c>).
    /// </summary>
    /// <param name="source">The source type.</param>
    /// <param name="destination">The destination type.</param>
    /// <param name="map">The configuration.</param>
    private MappingReads Configured(ITypeSymbol source, ITypeSymbol destination, MapConfiguration map)
    {
        if (map.Converter is { } converter)
        {
            return new MappingReads(converter.Paths, converter.Callbacks, true);
        }

        var paths = ImmutableArray.CreateBuilder<string>();
        var callbacks = ImmutableArray.CreateBuilder<MapperCallback>();
        foreach (var member in DestinationMembers(destination))
        {
            if (map.Members.TryGetValue(member.Name, out var mapped))
            {
                if (mapped is not null)
                {
                    AddUnique(paths, mapped.Paths);
                    callbacks.AddRange(mapped.Callbacks);
                }

                continue;
            }

            var own = MemberReads(source, member, MaxDepth).ToList();
            if (own.Count > 0 || HasProperty(source, member.Name))
            {
                AddUnique(paths, own);
                continue;
            }

            AddUnique(paths, IncludedMemberReads(map, member));
        }

        return new MappingReads(paths.ToImmutable(), callbacks.ToImmutable(), true);
    }

    /// <summary>
    /// The paths that fill a destination member from the first included source member that can fill it by the
    /// conventions: the included member itself, then what the conventions read on it.
    /// </summary>
    /// <param name="map">The configuration.</param>
    /// <param name="member">A destination member the source itself cannot fill.</param>
    private IEnumerable<string> IncludedMemberReads(MapConfiguration map, IPropertySymbol member)
    {
        foreach (var (path, type) in map.IncludedMembers)
        {
            var reads = MemberReads(type, member, MaxDepth).ToList();
            if (reads.Count == 0 && !HasProperty(type, member.Name))
            {
                continue;
            }

            yield return path;
            foreach (var read in reads)
            {
                yield return path + "." + read;
            }

            yield break;
        }
    }

    /// <summary>The paths the conventions read for every destination member.</summary>
    /// <param name="source">The source type.</param>
    /// <param name="destination">The destination type.</param>
    /// <param name="depth">How deep nested objects are still followed.</param>
    private ImmutableArray<string> Convention(ITypeSymbol source, ITypeSymbol destination, int depth)
    {
        var paths = ImmutableArray.CreateBuilder<string>();
        foreach (var member in DestinationMembers(destination))
        {
            AddUnique(paths, MemberReads(source, member, depth));
        }

        return paths.ToImmutable();
    }

    /// <summary>
    /// The navigation paths the conventions read for one destination member: a navigation of the
    /// same name (and, for an object or collection member, what mapping its type reads), or the
    /// navigation a flattened name starts with (<c>CustomerName</c> reads <c>Customer</c>, then <c>Name</c> on it).
    /// </summary>
    /// <param name="source">The source type.</param>
    /// <param name="member">The destination member.</param>
    /// <param name="depth">How deep nested objects are still followed.</param>
    private IEnumerable<string> MemberReads(ITypeSymbol source, IPropertySymbol member, int depth)
    {
        var navigations = entities.NavigationsOf(source);
        foreach (var navigation in navigations)
        {
            if (navigation.Property.Name != member.Name)
            {
                continue;
            }

            yield return navigation.Property.Name;
            var nested = symbols.ElementType(member.Type) ?? member.Type;
            if (depth > 1 && nested is INamedTypeSymbol { TypeKind: TypeKind.Class, SpecialType: SpecialType.None } && !entities.IsEntity(nested))
            {
                foreach (var inner in Convention(navigation.Target, nested, depth - 1))
                {
                    yield return navigation.Property.Name + "." + inner;
                }
            }

            yield break;
        }

        if (HasProperty(source, member.Name))
        {
            yield break;
        }

        foreach (var navigation in navigations)
        {
            if (member.Name.Length <= navigation.Property.Name.Length || !member.Name.StartsWith(navigation.Property.Name, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = member.Name.Substring(navigation.Property.Name.Length);
            if (HasProperty(navigation.Target, rest))
            {
                yield return navigation.Property.Name;
                foreach (var deeper in entities.NavigationsOf(navigation.Target))
                {
                    if (deeper.Property.Name == rest)
                    {
                        yield return navigation.Property.Name + "." + rest;
                    }
                }

                yield break;
            }
        }
    }

    /// <summary>Whether a type (or a base type) has a property of that name.</summary>
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

    /// <summary>The public instance properties a mapper fills on the destination.</summary>
    /// <param name="destination">The destination type.</param>
    private static IEnumerable<IPropertySymbol> DestinationMembers(ITypeSymbol destination)
    {
        for (var current = destination; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IPropertySymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public, IsIndexer: false } property)
                {
                    yield return property;
                }
            }
        }
    }

    /// <summary>Adds paths not already present.</summary>
    /// <param name="builder">The paths so far.</param>
    /// <param name="paths">More paths.</param>
    private static void AddUnique(ImmutableArray<string>.Builder builder, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!builder.Contains(path))
            {
                builder.Add(path);
            }
        }
    }

    /// <summary>
    /// Reads every AutoMapper <c>CreateMap&lt;TSource, TDestination&gt;()</c> and Mapster
    /// <c>NewConfig</c>/<c>ForType</c> in the compilation, with the member overrides chained on them.
    /// Only files that mention one of them get a semantic model.
    /// </summary>
    /// <param name="compilation">The compilation.</param>
    private static Dictionary<(MapperLibrary, ITypeSymbol, ITypeSymbol), MapConfiguration> CollectMaps(Compilation compilation)
    {
        var maps = new Dictionary<(MapperLibrary, ITypeSymbol, ITypeSymbol), MapConfiguration>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var text = tree.GetText().ToString();
            if (!text.Contains("CreateMap") && !text.Contains("NewConfig") && !text.Contains("ForType"))
            {
                continue;
            }

#pragma warning disable RS1030 // Mapping profiles live in other files than the mapping calls; this runs once per compilation.
            var model = compilation.GetSemanticModel(tree);
#pragma warning restore RS1030
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is not InvocationExpressionSyntax syntax
                    || ConfigurationMethodName(syntax) is not ("CreateMap" or "NewConfig" or "ForType")
                    || model.GetOperation(node) is not IInvocationOperation { Type: INamedTypeSymbol { TypeArguments.Length: 2 } expression } configuration
                    || MapOf(configuration, expression) is not { } library)
                {
                    continue;
                }

                var key = (library, expression.TypeArguments[0].OriginalDefinition, expression.TypeArguments[1].OriginalDefinition);
                if (!maps.TryGetValue(key, out var map))
                {
                    map = new MapConfiguration();
                    maps[key] = map;
                }

                // Calls chained on the configuration call configure the same map as long as they return
                // the same expression type; ReverseMap starts the opposite map and ends the chain, and
                // ConvertUsing, which returns nothing, ends it too.
                for (IOperation current = configuration;
                     current.Parent is IInvocationOperation chained && chained.Instance == current
                     && (SymbolEqualityComparer.Default.Equals(chained.Type, expression) || chained.TargetMethod.Name == "ConvertUsing");
                     current = chained)
                {
                    if (library == MapperLibrary.AutoMapper)
                    {
                        ReadAutoMapperCall(chained, map);
                    }
                    else
                    {
                        ReadMapsterOverride(chained, map.Members);
                    }
                }
            }
        }

        return maps;
    }

    /// <summary>The name a configuration call is made by, generic or not, with or without a receiver.</summary>
    /// <param name="syntax">The invocation.</param>
    private static string? ConfigurationMethodName(InvocationExpressionSyntax syntax) => syntax.Expression switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// Which library's map a configuration call starts: AutoMapper's <c>CreateMap</c> returning
    /// <c>IMappingExpression&lt;TSource, TDestination&gt;</c>, or Mapster's <c>NewConfig</c>/<c>ForType</c>
    /// returning <c>TypeAdapterSetter&lt;TSource, TDestination&gt;</c>. Null for anything else.
    /// </summary>
    /// <param name="configuration">The call.</param>
    /// <param name="expression">The type it returns.</param>
    private static MapperLibrary? MapOf(IInvocationOperation configuration, INamedTypeSymbol expression)
        => (configuration.TargetMethod.ContainingNamespace?.ToDisplayString(), configuration.TargetMethod.Name, expression.Name) switch
        {
            ("AutoMapper", "CreateMap", "IMappingExpression") => MapperLibrary.AutoMapper,
            ("Mapster", "NewConfig" or "ForType", "TypeAdapterSetter") => MapperLibrary.Mapster,
            _ => null,
        };

    /// <summary>
    /// Reads one call chained on <c>CreateMap</c>: <c>ForMember</c>, <c>ForPath</c>, <c>IncludeMembers</c> or
    /// <c>ConvertUsing</c>. A <c>ForPath</c> takes its root destination member away from the conventions, whether it
    /// maps the path or ignores it: AutoMapper then leaves the root member to the path maps alone.
    /// </summary>
    /// <param name="call">A call chained on <c>CreateMap</c>.</param>
    /// <param name="map">Where the configuration goes.</param>
    private static void ReadAutoMapperCall(IInvocationOperation call, MapConfiguration map)
    {
        switch (call.TargetMethod.Name)
        {
            case "ForMember" when call.Arguments.Length >= 2 && DestinationMember(call.Arguments[0].Value) is { } member:
                if (TryMemberOptions(call.Arguments[1].Value, out var mapped))
                {
                    map.Members[member] = mapped;
                }

                return;
            case "ForPath" when call.Arguments.Length >= 2 && DestinationRoot(call.Arguments[0].Value) is { } root:
                TryMemberOptions(call.Arguments[1].Value, out var pathMapped);
                var options = pathMapped ?? MemberOverride.Empty;
                map.Members[root] = map.Members.TryGetValue(root, out var existing) && existing is not null ? existing.Merge(options) : options;
                return;
            case "IncludeMembers":
                foreach (var argument in call.Arguments)
                {
                    var lambdas = argument.Value is IArrayCreationOperation { Initializer: { } initializer } ? initializer.ElementValues : ImmutableArray.Create(argument.Value);
                    foreach (var value in lambdas)
                    {
                        if (Operations.AsLambda(value) is { } lambda && MemberChain(lambda) is { } path && Operations.StripConversions(Operations.LambdaResult(lambda))?.Type is { } type)
                        {
                            map.IncludedMembers.Add((path, type));
                        }
                    }
                }

                return;
            case "ConvertUsing":
                map.Converter = Conversion(call);
                return;
        }
    }

    /// <summary>
    /// What a member options lambda (<c>o =&gt; o.MapFrom(...)</c>) maps the member from: null for <c>Ignore()</c>; the
    /// paths a <c>MapFrom</c> lambda reads; a value resolver's <c>Resolve</c> (<c>MapFrom&lt;TResolver&gt;()</c>), with
    /// the source member it is given for a member value resolver (<c>MapFrom&lt;TResolver, Customer&gt;(s =&gt; s.Customer)</c>).
    /// Options that set neither (<c>Condition</c> alone) leave the member to the conventions: false.
    /// </summary>
    /// <param name="value">The options argument.</param>
    /// <param name="mapped">What the member is mapped from; null when it is ignored.</param>
    private static bool TryMemberOptions(IOperation value, out MemberOverride? mapped)
    {
        mapped = null;
        if (Operations.AsLambda(value) is not { } options)
        {
            return false;
        }

        foreach (var operation in options.Body.Descendants())
        {
            if (operation is IInvocationOperation { TargetMethod.Name: "Ignore" })
            {
                return true;
            }

            if (operation is not IInvocationOperation { TargetMethod.Name: "MapFrom" } mapFrom)
            {
                continue;
            }

            var sourceLambda = mapFrom.Arguments.Length > 0 ? Operations.AsLambda(mapFrom.Arguments[0].Value) : null;
            var resolver = mapFrom.TargetMethod.TypeArguments.Length > 0 ? mapFrom.TargetMethod.TypeArguments[0] : null;
            if (resolver is null || CallbackOf(resolver, "IValueResolver", "IMemberValueResolver") is not { } resolve)
            {
                resolver = mapFrom.Arguments.Length > 0 ? Operations.StripConversions(mapFrom.Arguments[0].Value)?.Type : null;
                resolve = resolver is null ? null : CallbackOf(resolver, "IValueResolver", "IMemberValueResolver");
            }

            if (resolve is null)
            {
                mapped = sourceLambda is null ? MemberOverride.Empty : new MemberOverride(NavigationPaths(sourceLambda), ImmutableArray<MapperCallback>.Empty);
                return true;
            }

            var callbacks = ImmutableArray.CreateBuilder<MapperCallback>();
            callbacks.Add(new MapperCallback(resolve, 0, ""));
            var paths = ImmutableArray<string>.Empty;
            if (resolve.Parameters.Length == 5 && sourceLambda is not null && MemberChain(sourceLambda) is { } sourceMember)
            {
                callbacks.Add(new MapperCallback(resolve, 2, sourceMember));
                paths = NavigationPaths(sourceLambda).Add(sourceMember);
            }

            mapped = new MemberOverride(paths, callbacks.ToImmutable());
            return true;
        }

        return false;
    }

    /// <summary>
    /// What a <c>ConvertUsing</c> reads: a type converter's <c>Convert</c> (<c>ConvertUsing&lt;TConverter&gt;()</c> or an
    /// instance), or the paths a conversion lambda reads on the source.
    /// </summary>
    /// <param name="call">The <c>ConvertUsing</c> call.</param>
    private static MemberOverride Conversion(IInvocationOperation call)
    {
        var converter = call.TargetMethod.TypeArguments.Length > 0 ? call.TargetMethod.TypeArguments[0] : null;
        if (converter is null && call.Arguments.Length > 0)
        {
            converter = Operations.StripConversions(call.Arguments[0].Value)?.Type;
        }

        if (converter is not null && CallbackOf(converter, "ITypeConverter") is { } convert)
        {
            return new MemberOverride(ImmutableArray<string>.Empty, ImmutableArray.Create(new MapperCallback(convert, 0, "")));
        }

        return call.Arguments.Length > 0 && Operations.AsLambda(call.Arguments[0].Value) is { } lambda
            ? new MemberOverride(NavigationPaths(lambda), ImmutableArray<MapperCallback>.Empty)
            : MemberOverride.Empty;
    }

    /// <summary>
    /// The method of an AutoMapper callback interface a type implements (<c>Resolve</c> of <c>IValueResolver</c>,
    /// <c>Convert</c> of <c>ITypeConverter</c>), as the type implements it; null when it implements none of them.
    /// </summary>
    /// <param name="type">The resolver or converter type.</param>
    /// <param name="interfaces">The interface names accepted.</param>
    private static IMethodSymbol? CallbackOf(ITypeSymbol type, params string[] interfaces)
    {
        foreach (var candidate in type.AllInterfaces)
        {
            if (candidate.ContainingNamespace?.ToDisplayString() != "AutoMapper" || Array.IndexOf(interfaces, candidate.Name) < 0)
            {
                continue;
            }

            foreach (var member in candidate.GetMembers())
            {
                if (member is IMethodSymbol && type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation)
                {
                    return implementation;
                }
            }
        }

        return null;
    }

    /// <summary>The first destination member of a <c>ForPath</c> lambda (<c>d =&gt; d.Buyer.Name</c> gives <c>Buyer</c>).</summary>
    /// <param name="value">The argument value.</param>
    private static string? DestinationRoot(IOperation value)
    {
        if (Operations.AsLambda(value) is not { } lambda)
        {
            return null;
        }

        string? root = null;
        for (var current = Operations.StripConversions(Operations.LambdaResult(lambda)); current is IPropertyReferenceOperation property; current = property.Instance)
        {
            root = property.Property.Name;
        }

        return root;
    }

    /// <summary>The whole member chain a lambda returns on its parameter (<c>s =&gt; s.Customer.Address</c> gives <c>Customer.Address</c>), or null.</summary>
    /// <param name="lambda">The lambda.</param>
    private static string? MemberChain(IAnonymousFunctionOperation lambda)
    {
        var segments = new List<string>();
        var current = Operations.StripConversions(Operations.LambdaResult(lambda));
        while (current is IPropertyReferenceOperation property)
        {
            segments.Insert(0, property.Property.Name);
            current = property.Instance;
        }

        return current is IParameterReferenceOperation && segments.Count > 0 ? string.Join(".", segments) : null;
    }

    /// <summary>
    /// Reads one Mapster override chained on <c>NewConfig</c>: <c>Map(d =&gt; d.X, s =&gt; ...)</c> (or
    /// <c>Map("X", s =&gt; ...)</c>) and <c>Ignore(d =&gt; d.X, ...)</c> (or <c>Ignore("X", ...)</c>).
    /// </summary>
    /// <param name="call">A call chained on the configuration.</param>
    /// <param name="overrides">Where the overrides go.</param>
    private static void ReadMapsterOverride(IInvocationOperation call, Dictionary<string, MemberOverride?> overrides)
    {
        if (call.TargetMethod.Name == "Map"
            && call.Arguments.Length >= 2
            && DestinationMember(call.Arguments[0].Value) is { } member
            && Operations.AsLambda(call.Arguments[1].Value) is { } sourceLambda)
        {
            overrides[member] = new MemberOverride(NavigationPaths(sourceLambda), ImmutableArray<MapperCallback>.Empty);
            return;
        }

        if (call.TargetMethod.Name != "Ignore")
        {
            return;
        }

        foreach (var argument in call.Arguments)
        {
            var values = argument.Value is IArrayCreationOperation { Initializer: { } initializer } ? initializer.ElementValues : ImmutableArray.Create(argument.Value);
            foreach (var value in values)
            {
                if (DestinationMember(value) is { } ignored)
                {
                    overrides[ignored] = null;
                }
            }
        }
    }

    /// <summary>The destination member a configuration names: <c>d =&gt; d.X</c> (boxed or not) or the string <c>"X"</c>.</summary>
    /// <param name="value">The argument value.</param>
    private static string? DestinationMember(IOperation value)
    {
        if (Operations.StripConversions(value)?.ConstantValue is { HasValue: true, Value: string name })
        {
            return name;
        }

        return Operations.AsLambda(value) is { } lambda
               && Operations.StripConversions(Operations.LambdaResult(lambda)) is IPropertyReferenceOperation { Property.Name: var member }
            ? member
            : null;
    }

    /// <summary>The member chains a <c>MapFrom</c> or conversion lambda reads on its parameter, without the last (scalar) member.</summary>
    /// <param name="lambda">The source lambda.</param>
    private static ImmutableArray<string> NavigationPaths(IAnonymousFunctionOperation lambda)
    {
        var paths = ImmutableArray.CreateBuilder<string>();
        foreach (var operation in lambda.Body.Descendants())
        {
            if (operation is not IPropertyReferenceOperation outer || outer.Parent is IPropertyReferenceOperation { Instance: var parentInstance } && parentInstance == outer)
            {
                continue;
            }

            var segments = new List<string>();
            var current = outer.Instance;
            while (current is IPropertyReferenceOperation property)
            {
                segments.Insert(0, property.Property.Name);
                current = property.Instance;
            }

            if (current is IParameterReferenceOperation && segments.Count > 0)
            {
                paths.Add(string.Join(".", segments));
            }
        }

        return paths.ToImmutable();
    }
}
