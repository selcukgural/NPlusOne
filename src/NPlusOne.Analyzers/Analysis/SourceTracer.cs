using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using NPlusOne.Analyzers.Model;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// Follows an expression back to where its value comes from: a <c>DbSet</c>, a query composed on
/// one, the results of running one, a parameter, or something in memory. Locals are followed through
/// their assignments, calls through the summaries of the methods they reach. One tracer serves one
/// operation tree (a method body) on one thread, and caches what it learns about its locals.
/// </summary>
/// <param name="session">The compilation-wide analysis state.</param>
/// <param name="owner">
/// The method whose body is traced; its parameters trace to <see cref="OriginKind.Parameter"/>, the
/// parameters of local functions inside it to <see cref="OriginKind.Unknown"/>.
/// </param>
internal sealed class SourceTracer(Session session, IMethodSymbol? owner)
{
    private readonly Dictionary<ILocalSymbol, SourceInfo> _locals = new(SymbolEqualityComparer.Default);

    /// <summary>Traces an expression with the session's default call depth.</summary>
    /// <param name="operation">The expression.</param>
    public SourceInfo Trace(IOperation? operation) => Trace(operation, session.Settings.MaxCallDepth);

    /// <summary>Traces an expression, following at most <paramref name="depth"/> calls.</summary>
    /// <param name="operation">The expression.</param>
    /// <param name="depth">How many calls deep the trace may still go.</param>
    public SourceInfo Trace(IOperation? operation, int depth)
    {
        var stripped = Operations.StripConversions(operation);

        // An ABP repository is an entity set, however it was obtained (injected, a base class's Repository, this in a
        // custom repository). No code fix adds an Include to it, so the query has no root location.
        if (stripped is not null && session.Symbols.IsAbpRepository(stripped.Type))
        {
            return SourceInfo.EfQuery;
        }

        return stripped switch
        {
            null => SourceInfo.Unknown,
            IAwaitOperation awaited => Trace(awaited.Operation, depth),
            IInvocationOperation invocation => TraceInvocation(invocation, depth),
            IPropertyReferenceOperation property => TraceProperty(property, depth),
            IArrayElementReferenceOperation element when session.Entities.IsEntity(element.Type) => Trace(element.ArrayReference, depth).Element(),
            IFieldReferenceOperation field when ElementParts.MemberOf(field) is { } part => TracePart(part.Instance, part.Member, depth),
            IFieldReferenceOperation field => IsDbSet(field.Type) ? SourceInfo.EfQueryAt(field) : session.AssignedSource(field.Field),
            ILocalReferenceOperation local => TraceLocal(local, depth),
            IParameterReferenceOperation parameter => TraceParameter(parameter, depth),
            IInstanceReferenceOperation input when Operations.PatternInput(input) is { } patterned => Trace(patterned, depth),
            IInstanceReferenceOperation when owner is { IsStatic: false } => SourceInfo.FromParameter(ParameterNavigationRead.This),
            IConditionalOperation { WhenFalse: not null } conditional => Trace(conditional.WhenTrue, depth).Combine(Trace(conditional.WhenFalse, depth)),
            ICoalesceOperation coalesce => Trace(coalesce.Value, depth).Combine(Trace(coalesce.WhenNull, depth)),
            IArrayCreationOperation or IObjectCreationOperation or ILiteralOperation => SourceInfo.NotEf,
            _ => SourceInfo.Unknown,
        };
    }

    /// <summary>
    /// The sequence a lambda's element parameter iterates over, when the lambda is passed to a
    /// method that calls it once per element (<c>orders.Select(o =&gt; ...)</c> gives <c>orders</c>).
    /// </summary>
    /// <param name="lambda">The lambda.</param>
    public IOperation? IteratedSource(IAnonymousFunctionOperation lambda)
    {
        if (CallbackSite.Of(lambda) is not { } site || !session.IsIterating(site.Invocation.TargetMethod, site.Parameter))
        {
            return null;
        }

        return SequenceArgument(site.Invocation);
    }

    /// <summary>The sequence an iterating call walks: the receiver of <c>List.ForEach</c>, otherwise the first sequence argument.</summary>
    /// <param name="invocation">A call to an iterating method.</param>
    public IOperation? SequenceArgument(IInvocationOperation invocation) => SequenceArgumentOf(invocation, session.Symbols);

    /// <summary>The sequence an iterating call walks: the receiver of <c>List.ForEach</c>, otherwise the first sequence argument.</summary>
    /// <param name="invocation">A call to an iterating method.</param>
    /// <param name="symbols">The known symbols.</param>
    public static IOperation? SequenceArgumentOf(IInvocationOperation invocation, KnownSymbols symbols)
    {
        if (invocation.Instance is not null && symbols.ElementType(invocation.Instance.Type) is not null)
        {
            return invocation.Instance;
        }

        foreach (var argument in invocation.Arguments)
        {
            if (symbols.ElementType(argument.Value.Type) is not null
                || symbols.ElementType(Operations.StripConversions(argument.Value)?.Type) is not null)
            {
                return argument.Value;
            }
        }

        return null;
    }

    /// <summary>Whether a type is <c>DbSet&lt;T&gt;</c> or derives from it.</summary>
    /// <param name="type">The type.</param>
    private bool IsDbSet(ITypeSymbol? type) => Model.KnownSymbols.InheritsFrom(type, session.Symbols.DbSet);

    /// <summary>Traces a call: query operators by their meaning, other methods through their summaries.</summary>
    /// <param name="invocation">The call.</param>
    /// <param name="depth">The remaining call depth.</param>
    private SourceInfo TraceInvocation(IInvocationOperation invocation, int depth)
    {
        var call = QueryCalls.Classify(invocation, session.Symbols);
        switch (call.Kind)
        {
            case QueryCallKind.Root:
                return invocation.TargetMethod.Name == "Set" ? SourceInfo.EfQueryAt(invocation) : SourceInfo.EfQuery;
            case QueryCallKind.Compose:
                {
                    var source = Trace(call.Source, depth);
                    if (call.NotEfWhenSourceIsNotQuery && !session.Symbols.IsQueryable(Operations.StripConversions(call.Source)?.Type))
                    {
                        return SourceInfo.NotEf;
                    }

                    if (call.IncludePath is { Length: > 0 } path)
                    {
                        source = source.WithInclude(path);
                    }

                    source = call.WithDetails(source);

                    source = invocation.TargetMethod.Name switch
                    {
                        "AsNoTracking" or "AsNoTrackingWithIdentityResolution" => source with { Untracked = true },
                        "AsTracking" => source with { Untracked = false },
                        "IgnoreAutoIncludes" => source with { IgnoresAutoIncludes = true },

                        // FromSql* only takes a DbSet: an Include added by the code fix goes after it.
                        "FromSqlRaw" or "FromSqlInterpolated" or "FromSql" => source with { Root = invocation.Syntax.GetLocation() },
                        _ => source,
                    };

                    return call.Projects ? source.Projected() : source;
                }

            case QueryCallKind.Execute:
                return call.WithDetails(Trace(call.Source, depth)).Materialized();
            case QueryCallKind.ExecuteAlways:
                return invocation.TargetMethod.Name is "Find" or "FindAsync"
                    ? SourceInfo.EfQuery.Materialized()
                    : SourceInfo.Unknown;
            case QueryCallKind.Save:
                return SourceInfo.Unknown;
            default:
                return TraceUserCall(invocation, depth);
        }
    }

    /// <summary>
    /// Traces a call to a method that is not a query operator: what its summary says it returns,
    /// with its parameters replaced by the caller's arguments.
    /// </summary>
    /// <param name="invocation">The call.</param>
    /// <param name="depth">The remaining call depth.</param>
    private SourceInfo TraceUserCall(IInvocationOperation invocation, int depth)
    {
        var method = invocation.TargetMethod;
        if (depth <= 0)
        {
            return SourceInfo.Unknown;
        }

        var returned = session.ReturnsOf(method);
        if (returned.Kind == OriginKind.Parameter)
        {
            var argument = ArgumentFor(invocation, returned.Parameter);
            var traced = Trace(argument, depth - 1);
            return traced.Kind == OriginKind.Ef ? traced with { Includes = traced.Includes.Union(returned.Includes) } : traced;
        }

        if (returned.Kind != OriginKind.Unknown)
        {
            return returned;
        }

        // A method without a visible body that returns IQueryable<Entity>, also in a task (ABP's GetQueryableAsync and
        // GetDbSetAsync), is a repository handing out an EF query.
        var queryable = session.Symbols.Unwrap(method.ReturnType);
        if (session.Symbols.IsQueryable(queryable) && session.Entities.IsEntity(session.Symbols.ElementType(queryable)))
        {
            return SourceInfo.EfQuery;
        }

        return SourceInfo.Unknown;
    }

    /// <summary>
    /// Traces a property: a <c>DbSet</c>, a navigation of a traced entity, an entity read from a list or dictionary
    /// by index (an element of it), the value of a key/value pair element, or a getter with a summary.
    /// </summary>
    /// <param name="property">The property reference.</param>
    /// <param name="depth">The remaining call depth.</param>
    private SourceInfo TraceProperty(IPropertyReferenceOperation property, int depth)
    {
        if (IsDbSet(property.Type))
        {
            return SourceInfo.EfQueryAt(property);
        }

        if (session.Entities.AsNavigation(property.Property) is { } navigation && property.Instance is not null)
        {
            var owner = Trace(property.Instance, depth);
            if (owner.Kind is OriginKind.Ef or OriginKind.Parameter)
            {
                return owner with { IsQuery = false, IsElement = false, Path = Join(owner.Path, navigation.Property.Name) };
            }

            return SourceInfo.Unknown;
        }

        if (property.Property.IsIndexer && property.Instance is { } list
            && session.Symbols.ElementType(list.Type) is not null && session.Entities.IsEntity(property.Type))
        {
            return Trace(list, depth).Element();
        }

        // A part of an element built per element; a property whose element is not built where the code shows how is
        // traced as any other property.
        if (ElementParts.MemberOf(property) is { } part && TracePart(part.Instance, part.Member, depth) is { Kind: not OriginKind.Unknown } traced)
        {
            return traced;
        }

        if (property.Property.GetMethod is { } getter && depth > 0)
        {
            var returned = session.ReturnsOf(getter);
            if (returned.Kind is OriginKind.Ef or OriginKind.NotEf)
            {
                return returned;
            }

            // An auto-property has no body to summarise: what it holds is what the type assigns to it.
            return Session.IsAutoProperty(property.Property) ? session.AssignedSource(property.Property) : SourceInfo.Unknown;
        }

        return SourceInfo.Unknown;
    }

    /// <summary>
    /// Traces a parameter: a method parameter stays a parameter (the caller decides), a lambda's
    /// element parameter is an element of the sequence the lambda is called for.
    /// </summary>
    /// <param name="reference">The parameter reference.</param>
    /// <param name="depth">The remaining call depth.</param>
    private SourceInfo TraceParameter(IParameterReferenceOperation reference, int depth)
    {
        var parameter = reference.Parameter;
        if (parameter.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } lambdaSymbol)
        {
            return owner is not null && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol?.OriginalDefinition, owner.OriginalDefinition)
                ? SourceInfo.FromParameter(parameter.Ordinal)
                : SourceInfo.Unknown;
        }

        for (var current = reference.Parent; current is not null; current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation lambda && SymbolEqualityComparer.Default.Equals(lambda.Symbol, lambdaSymbol))
            {
                return IteratedSource(lambda) is { } source ? Trace(source, depth).Element() : SourceInfo.Unknown;
            }
        }

        return SourceInfo.Unknown;
    }

    /// <summary>
    /// Traces a local through every assignment in its method: declarator initialisers, plain and
    /// <c>??=</c> assignments, and the collection of a <c>foreach</c> that declares it (for a part the loop
    /// deconstructs, what that part was built from).
    /// </summary>
    /// <param name="reference">The local reference.</param>
    /// <param name="depth">The remaining call depth.</param>
    private SourceInfo TraceLocal(ILocalReferenceOperation reference, int depth)
    {
        var local = reference.Local;
        if (_locals.TryGetValue(local, out var cached))
        {
            return cached;
        }

        _locals[local] = SourceInfo.Pending;
        var result = SourceInfo.Pending;
        foreach (var operation in Operations.Root(reference).Descendants())
        {
            switch (operation)
            {
                case IVariableDeclaratorOperation declarator when SymbolEqualityComparer.Default.Equals(declarator.Symbol, local):
                    if (declarator.GetVariableInitializer() is { } initializer)
                    {
                        result = result.Combine(Trace(initializer.Value, depth));
                    }

                    break;
                case ISimpleAssignmentOperation assignment when IsLocal(assignment.Target, local):
                    result = result.Combine(Trace(assignment.Value, depth));
                    break;
                case ICoalesceAssignmentOperation coalesce when IsLocal(coalesce.Target, local):
                    result = result.Combine(Trace(coalesce.Value, depth));
                    break;
                case IForEachLoopOperation { LoopControlVariable: IVariableDeclaratorOperation } loop when loop.Locals.Contains(local, SymbolEqualityComparer.Default):
                    result = result.Combine(Trace(loop.Collection, depth).Element());
                    break;
                case IForEachLoopOperation loop when loop.Locals.Contains(local, SymbolEqualityComparer.Default):
                    result = result.Combine(ElementParts.DeconstructedMember(loop, local, session.Symbols) is { } member
                        ? TraceResolved(ElementParts.Resolve(loop.Collection, member), depth)
                        : SourceInfo.Unknown);
                    break;
                case IDeconstructionAssignmentOperation or IDeclarationExpressionOperation when !ElementParts.IsLoopControlVariable(operation) && Mentions(operation, local):
                    result = result.Combine(SourceInfo.Unknown);
                    break;
            }
        }

        if (result.Kind == OriginKind.Pending)
        {
            result = SourceInfo.Unknown;
        }

        _locals[local] = result;
        return result;
    }

    /// <summary>
    /// Traces a part of a <c>foreach</c> element (<c>pair.Order</c>, <c>pair.Value</c>) to what the elements were built
    /// from; unknown for anything but the iteration variable of a <c>foreach</c>.
    /// </summary>
    /// <param name="instance">The element the part is read from.</param>
    /// <param name="member">The part.</param>
    /// <param name="depth">The remaining call depth.</param>
    private SourceInfo TracePart(IOperation instance, string member, int depth)
        => Operations.StripConversions(instance) switch
        {
            ILocalReferenceOperation local when ElementParts.LoopDeclaring(local.Local, local) is { } loop
                => TraceResolved(ElementParts.Resolve(loop.Collection, member), depth),
            IParameterReferenceOperation { Parameter.ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } } parameter
                when LambdaOf(parameter) is { } lambda && IteratedSource(lambda) is { } sequence
                => TraceResolved(ElementParts.Resolve(sequence, member), depth),
            _ => SourceInfo.Unknown,
        };

    /// <summary>The lambda that declares a parameter, found among the reference's ancestors.</summary>
    /// <param name="reference">The parameter reference.</param>
    private static IAnonymousFunctionOperation? LambdaOf(IParameterReferenceOperation reference)
    {
        for (var current = reference.Parent; current is not null; current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation lambda && SymbolEqualityComparer.Default.Equals(lambda.Symbol, reference.Parameter.ContainingSymbol))
            {
                return lambda;
            }
        }

        return null;
    }

    /// <summary>Traces what a part was built from: the expression put into each element, or an element of a sequence.</summary>
    /// <param name="part">The part, null when not known.</param>
    /// <param name="depth">The remaining call depth.</param>
    private SourceInfo TraceResolved(ElementPart? part, int depth)
    {
        return part switch
        {
            { Value: { } value } => Trace(value, depth),
            { ElementOf: { } sequence } => Trace(sequence, depth).Element(),
            _ => SourceInfo.Unknown,
        };
    }

    /// <summary>The argument passed for a parameter ordinal, named or positional.</summary>
    /// <param name="invocation">The call.</param>
    /// <param name="ordinal">The parameter ordinal.</param>
    private static IOperation? ArgumentFor(IInvocationOperation invocation, int ordinal)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter?.Ordinal == ordinal)
            {
                return argument.Value;
            }
        }

        return null;
    }

    /// <summary>Whether an assignment target is the given local.</summary>
    /// <param name="target">The assignment target.</param>
    /// <param name="local">The local.</param>
    private static bool IsLocal(IOperation target, ILocalSymbol local)
        => target is ILocalReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Local, local);

    /// <summary>Whether a deconstruction or declaration expression declares or assigns the local.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="local">The local.</param>
    private static bool Mentions(IOperation operation, ILocalSymbol local)
    {
        foreach (var descendant in operation.Descendants())
        {
            if (descendant is ILocalReferenceOperation { IsDeclaration: true } reference
                && SymbolEqualityComparer.Default.Equals(reference.Local, local))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Joins two navigation paths.</summary>
    /// <param name="left">The outer path, possibly empty.</param>
    /// <param name="right">The inner segment.</param>
    public static string Join(string left, string right) => left.Length == 0 ? right : left + "." + right;
}
