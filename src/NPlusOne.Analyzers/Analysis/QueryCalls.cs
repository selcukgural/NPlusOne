using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using NPlusOne.Analyzers.Model;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// Classifies invocations by what they do to a query: start one, compose on one, run one, or
/// save. This is the only place that knows the EF Core and LINQ method names; <see cref="AbpCalls"/>
/// knows ABP Framework's.
/// </summary>
internal static class QueryCalls
{
    /// <summary>LINQ operators whose result rows are not the source rows, so includes stop applying.</summary>
    private static readonly HashSet<string> Projections =
    [
        "Select", "SelectMany", "GroupBy", "Join", "GroupJoin", "Zip", "Cast", "OfType", "LeftJoin", "RightJoin",
    ];

    /// <summary>Classifies an invocation.</summary>
    /// <param name="invocation">The invocation.</param>
    /// <param name="symbols">The compilation's known symbols.</param>
    public static QueryCall Classify(IInvocationOperation invocation, KnownSymbols symbols)
    {
        var method = invocation.TargetMethod;
        var type = method.ContainingType?.OriginalDefinition;
        if (type is null)
        {
            return QueryCall.None;
        }

        var source = FirstArgument(invocation);

        if (Is(type, symbols.Queryable))
        {
            if (method.Name == "AsQueryable")
            {
                return new QueryCall(QueryCallKind.Compose, source, NotEfWhenSourceIsNotQuery: true);
            }

            return symbols.IsQueryable(method.ReturnType)
                ? new QueryCall(QueryCallKind.Compose, source, Projects: Projections.Contains(method.Name))
                : new QueryCall(QueryCallKind.Execute, source);
        }

        if (Is(type, symbols.EfQueryableExtensions) || Is(type, symbols.RelationalQueryableExtensions))
        {
            return ClassifyEfOperator(invocation, symbols, source);
        }

        if (Is(type, symbols.Enumerable) || Is(type, symbols.AsyncEnumerable))
        {
            return ClassifyEnumerable(method, symbols, source);
        }

        // await foreach (var o in query.AsAsyncEnumerable().WithCancellation(ct)) walks the same query.
        if (method.Name is "WithCancellation" or "ConfigureAwait" && Model.Symbols.FullName(type) == "System.Threading.Tasks.TaskAsyncEnumerableExtensions")
        {
            return new QueryCall(QueryCallKind.Compose, source);
        }

        if (Is(type, symbols.RelationalDatabaseFacadeExtensions))
        {
            return method.Name.StartsWith("SqlQuery", StringComparison.Ordinal)
                ? new QueryCall(QueryCallKind.Root)
                : method.Name.StartsWith("ExecuteSql", StringComparison.Ordinal)
                    ? new QueryCall(QueryCallKind.ExecuteAlways)
                    : IsBeginTransaction(method)
                        ? new QueryCall(QueryCallKind.Save)
                        : QueryCall.None;
        }

        return AbpCalls.Classify(invocation, symbols, source) ?? ClassifyInstanceMethod(method, symbols, source);
    }

    /// <summary>
    /// Whether a method starts a transaction (<c>Database.BeginTransaction</c>, <c>BeginTransactionAsync</c>, the
    /// relational overloads taking an isolation level): its begin and commit are round trips, and the work in it commits
    /// on its own, like a <c>SaveChanges</c>.
    /// </summary>
    /// <param name="method">The method.</param>
    public static bool IsBeginTransaction(IMethodSymbol method) => method.Name is "BeginTransaction" or "BeginTransactionAsync";

    /// <summary>The <c>this</c> argument of an extension call, or the receiver of an instance call.</summary>
    /// <param name="invocation">The invocation.</param>
    public static IOperation? FirstArgument(IInvocationOperation invocation)
    {
        if (invocation.Instance is not null)
        {
            return invocation.Instance;
        }

        return invocation.Arguments.Length > 0 ? invocation.Arguments[0].Value : null;
    }

    /// <summary>
    /// The full navigation path an <c>Include</c> or <c>ThenInclude</c> call adds, such as
    /// <c>Lines.Product</c> for <c>Include(o =&gt; o.Lines).ThenInclude(l =&gt; l.Product)</c>; null when it cannot
    /// be read (a dynamic string, an unusual lambda).
    /// </summary>
    /// <param name="invocation">An <c>Include</c> or <c>ThenInclude</c> call.</param>
    public static string? IncludePathOf(IInvocationOperation invocation)
    {
        if (invocation.Arguments.Length < 2)
        {
            return null;
        }

        var argument = Operations.StripConversions(invocation.Arguments[1].Value);
        string? relative;
        if (argument is ILiteralOperation { ConstantValue: { HasValue: true, Value: string text } })
        {
            relative = text;
        }
        else
        {
            relative = SelectorPath(argument);
        }

        if (relative is null || invocation.TargetMethod.Name != "ThenInclude")
        {
            return relative;
        }

        return Operations.StripConversions(FirstArgument(invocation)) is IInvocationOperation { TargetMethod.Name: "Include" or "ThenInclude" } previous
               && IncludePathOf(previous) is { } parent
            ? parent + "." + relative
            : null;
    }

    /// <summary>
    /// The member path a selector lambda reads from its parameter: <c>o =&gt; o.Customer.Address</c> gives
    /// <c>Customer.Address</c>; null when the argument is not such a lambda.
    /// </summary>
    /// <param name="argument">The selector argument, conversions included.</param>
    public static string? SelectorPath(IOperation? argument)
        => Operations.AsLambda(Operations.StripConversions(argument)) is { } lambda && Operations.LambdaResult(lambda) is { } body
            ? MemberPath(body, lambda.Symbol.Parameters.Length > 0 ? lambda.Symbol.Parameters[0] : null)
            : null;

    /// <summary>Whether a definition is the given known type.</summary>
    /// <param name="type">A type definition.</param>
    /// <param name="known">A known symbol, possibly missing from the compilation.</param>
    private static bool Is(INamedTypeSymbol type, INamedTypeSymbol? known)
        => known is not null && SymbolEqualityComparer.Default.Equals(type, known);

    /// <summary>EF Core's own operators: composing ones return a query, the rest run it.</summary>
    /// <param name="invocation">The invocation.</param>
    /// <param name="symbols">The known symbols.</param>
    /// <param name="source">The query the operator works on.</param>
    private static QueryCall ClassifyEfOperator(IInvocationOperation invocation, KnownSymbols symbols, IOperation? source)
    {
        var method = invocation.TargetMethod;
        if (method.Name == "ToQueryString")
        {
            return QueryCall.None;
        }

        if (method.Name is "Include" or "ThenInclude")
        {
            return new QueryCall(QueryCallKind.Compose, source, IncludePath: IncludePathOf(invocation) ?? "");
        }

        return symbols.IsQueryable(method.ReturnType)
            ? new QueryCall(QueryCallKind.Compose, source)
            : new QueryCall(QueryCallKind.Execute, source);
    }

    /// <summary>
    /// LINQ to Objects: deferred operators pass their source through (they run SQL only when the
    /// result is enumerated), the others enumerate it, which runs SQL when the source is a query.
    /// </summary>
    /// <param name="method">The operator.</param>
    /// <param name="symbols">The known symbols.</param>
    /// <param name="source">The sequence it works on.</param>
    private static QueryCall ClassifyEnumerable(IMethodSymbol method, KnownSymbols symbols, IOperation? source)
    {
        if (method.Name is "AsEnumerable" or "AsAsyncEnumerable")
        {
            return new QueryCall(QueryCallKind.Compose, source);
        }

        var returnsSequence = symbols.ElementType(method.ReturnType) is not null
                              && method.Name is not ("ToList" or "ToArray" or "ToHashSet" or "ToDictionary" or "ToLookup"
                                  or "ToListAsync" or "ToArrayAsync" or "ToHashSetAsync" or "ToDictionaryAsync" or "ToLookupAsync"
                                  or "ToImmutableArray" or "ToImmutableList" or "ToFrozenSet" or "ToFrozenDictionary");
        return returnsSequence
            ? new QueryCall(QueryCallKind.Compose, source, Projects: Projections.Contains(method.Name))
            : new QueryCall(QueryCallKind.Execute, source);
    }

    /// <summary>
    /// Instance methods of EF Core types: <c>Set</c>, <c>Find</c>, <c>SaveChanges</c>, explicit loading, and
    /// <c>DbSet&lt;T&gt;</c>'s own <c>AsAsyncEnumerable</c>, which streams the set's query.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <param name="symbols">The known symbols.</param>
    /// <param name="source">The receiver.</param>
    private static QueryCall ClassifyInstanceMethod(IMethodSymbol method, KnownSymbols symbols, IOperation? source)
    {
        var type = method.ContainingType;
        if (KnownSymbols.InheritsFrom(type, symbols.DbContext))
        {
            return method.Name switch
            {
                "Set" => new QueryCall(QueryCallKind.Root),
                "Find" or "FindAsync" => new QueryCall(QueryCallKind.ExecuteAlways),
                "SaveChanges" or "SaveChangesAsync" => new QueryCall(QueryCallKind.Save),
                _ => QueryCall.None,
            };
        }

        if (KnownSymbols.InheritsFrom(type, symbols.DatabaseFacade))
        {
            return IsBeginTransaction(method) ? new QueryCall(QueryCallKind.Save) : QueryCall.None;
        }

        if (KnownSymbols.InheritsFrom(type, symbols.DbSet))
        {
            return method.Name switch
            {
                "Find" or "FindAsync" => new QueryCall(QueryCallKind.ExecuteAlways),
                "AsAsyncEnumerable" => new QueryCall(QueryCallKind.Compose, source),
                _ => QueryCall.None,
            };
        }

        if (KnownSymbols.InheritsFrom(type, symbols.NavigationEntry))
        {
            return method.Name switch
            {
                "Load" or "LoadAsync" => new QueryCall(QueryCallKind.ExecuteAlways),
                "Query" => new QueryCall(QueryCallKind.Root),
                _ => QueryCall.None,
            };
        }

        if (KnownSymbols.InheritsFrom(type, symbols.EntityEntry))
        {
            return method.Name is "Reload" or "ReloadAsync" or "GetDatabaseValues" or "GetDatabaseValuesAsync"
                ? new QueryCall(QueryCallKind.ExecuteAlways)
                : QueryCall.None;
        }

        return QueryCall.None;
    }

    /// <summary>
    /// The member path a lambda body reads from its parameter: <c>o.Customer.Address</c> gives
    /// <c>Customer.Address</c>. Filtered includes (<c>o.Lines.Where(...)</c>) give the collection's path.
    /// </summary>
    /// <param name="body">The lambda's result expression.</param>
    /// <param name="parameter">The lambda's parameter.</param>
    private static string? MemberPath(IOperation body, IParameterSymbol? parameter)
    {
        var current = Operations.StripConversions(body);
        while (current is IInvocationOperation filter && filter.Arguments.Length > 0)
        {
            current = Operations.StripConversions(filter.Arguments[0].Value);
        }

        var segments = new List<string>();
        while (current is IPropertyReferenceOperation property)
        {
            segments.Add(property.Property.Name);
            current = Operations.StripConversions(property.Instance);
        }

        if (current is not IParameterReferenceOperation reference
            || !SymbolEqualityComparer.Default.Equals(reference.Parameter, parameter)
            || segments.Count == 0)
        {
            return null;
        }

        segments.Reverse();
        return string.Join(".", segments);
    }
}
