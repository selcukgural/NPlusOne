using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using NPlusOne.Analyzers.Model;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// Classifies the calls of ABP Framework's data access: the methods of its repositories, their extension methods, and
/// <c>AsyncExecuter</c>. Their bodies are in ABP's assemblies, and an ABP application layer references no EF Core, so
/// this is what tells the analysis that <c>await _bookRepository.GetAsync(id)</c> runs a query. The repository itself
/// is the query's root (<see cref="SourceTracer"/> traces any repository-typed expression to an EF query).
/// </summary>
internal static class AbpCalls
{
    /// <summary>
    /// Repository methods (and <c>RepositoryExtensions</c>) that run a query whatever their arguments: by key, by predicate,
    /// a page, a count, an existence check, explicit loading of a navigation.
    /// </summary>
    private static readonly ImmutableHashSet<string> Queries = ImmutableHashSet.Create(
        StringComparer.Ordinal, "GetAsync", "FindAsync", "GetListAsync", "GetCountAsync", "GetPagedListAsync", "DeleteDirectAsync",
        "EnsureExistsAsync", "EnsureCollectionLoadedAsync", "EnsurePropertyLoadedAsync");

    /// <summary>Repository methods that change entities: a round trip only with <c>autoSave: true</c>, which saves at once.</summary>
    private static readonly ImmutableHashSet<string> Writes = ImmutableHashSet.Create(
        StringComparer.Ordinal, "InsertAsync", "InsertManyAsync", "UpdateAsync", "UpdateManyAsync", "DeleteAsync", "DeleteManyAsync", "HardDeleteAsync");

    /// <summary>Classifies an ABP call; null when the call is not one of ABP's data access methods.</summary>
    /// <param name="invocation">The invocation.</param>
    /// <param name="symbols">The compilation's known symbols.</param>
    /// <param name="source">The receiver, or the <c>this</c> argument of an extension method.</param>
    public static QueryCall? Classify(IInvocationOperation invocation, KnownSymbols symbols, IOperation? source)
    {
        if (symbols.AbpRepository is null)
        {
            return null;
        }

        var method = invocation.TargetMethod;
        var type = method.ContainingType.OriginalDefinition;

        // AsyncExecuter.FirstOrDefaultAsync(query) runs the query it is given; repository.CountAsync(predicate) the repository's.
        if (Is(type, symbols.AbpAsyncQueryableExecuter))
        {
            return new QueryCall(QueryCallKind.Execute, invocation.Arguments.Length > 0 ? invocation.Arguments[0].Value : null);
        }

        if (Is(type, symbols.AbpRepositoryAsyncExtensions))
        {
            return new QueryCall(QueryCallKind.Execute, source);
        }

        var extension = Is(type, symbols.AbpRepositoryExtensions);
        if (!extension && (!symbols.IsAbpRepository(type) || !method.OriginalDefinition.DeclaringSyntaxReferences.IsEmpty))
        {
            // A method the application declares (a custom repository's own query) is followed through its body.
            return null;
        }

        if (Queries.Contains(method.Name))
        {
            return new QueryCall(QueryCallKind.Execute, source, UnknownDetails: LoadsDetails(invocation));
        }

        // GetQueryableAsync() and GetDbSetAsync() need nothing here: a bodyless method handing out a query of entities in a
        // task is an EF query (SourceTracer). WithDetails also says which navigations the query loads.
        if (method.Name is "WithDetails" or "WithDetailsAsync")
        {
            return Details(invocation, source);
        }

        if (method.Name == "SaveChangesAsync")
        {
            return new QueryCall(QueryCallKind.Save);
        }

        if (!Writes.Contains(method.Name))
        {
            return null;
        }

        // DeleteAsync(id), DeleteManyAsync(ids) and DeleteAsync(predicate) load the entities before deleting them.
        if (method.Parameters.Length > (extension ? 1 : 0) && method.Parameters[extension ? 1 : 0].Name is "id" or "ids" or "predicate")
        {
            return new QueryCall(QueryCallKind.Execute, source);
        }

        return SavesAtOnce(invocation) ? new QueryCall(QueryCallKind.Save) : QueryCall.None;
    }

    /// <summary>
    /// <c>WithDetails(x =&gt; x.Author, ...)</c> includes the navigations it names; <c>WithDetails()</c> the ones the
    /// application configured, which the analysis cannot read.
    /// </summary>
    /// <param name="invocation">The <c>WithDetails</c> or <c>WithDetailsAsync</c> call.</param>
    /// <param name="source">The repository.</param>
    private static QueryCall Details(IInvocationOperation invocation, IOperation? source)
    {
        var paths = ImmutableArray.CreateBuilder<string>();
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Value is not IArrayCreationOperation { Initializer: { } initializer })
            {
                continue;
            }

            foreach (var selector in initializer.ElementValues)
            {
                if (QueryCalls.SelectorPath(selector) is not { } path)
                {
                    return new QueryCall(QueryCallKind.Compose, source, UnknownDetails: true);
                }

                paths.Add(path);
            }
        }

        return paths.Count == 0
            ? new QueryCall(QueryCallKind.Compose, source, UnknownDetails: true)
            : new QueryCall(QueryCallKind.Compose, source, DetailPaths: paths.ToImmutable());
    }

    /// <summary>
    /// Whether a query method loads the details the application configured: its <c>includeDetails</c> argument is
    /// <c>true</c> (the default of <c>GetAsync</c> and <c>FindAsync</c>) or not a constant.
    /// </summary>
    /// <param name="invocation">The call.</param>
    private static bool LoadsDetails(IInvocationOperation invocation)
        => ArgumentNamed(invocation, "includeDetails") is { } argument
           && argument.Value.ConstantValue is not { HasValue: true, Value: false };

    /// <summary>Whether a write saves at once: its <c>autoSave</c> argument is the constant <c>true</c>.</summary>
    /// <param name="invocation">The call.</param>
    private static bool SavesAtOnce(IInvocationOperation invocation)
        => ArgumentNamed(invocation, "autoSave")?.Value.ConstantValue is { HasValue: true, Value: true };

    /// <summary>The argument of the parameter with the given name, a default value included.</summary>
    /// <param name="invocation">The call.</param>
    /// <param name="name">The parameter's name.</param>
    private static IArgumentOperation? ArgumentNamed(IInvocationOperation invocation, string name)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter?.Name == name)
            {
                return argument;
            }
        }

        return null;
    }

    /// <summary>Whether a definition is the given known type.</summary>
    /// <param name="type">A type definition.</param>
    /// <param name="known">A known symbol, possibly missing from the compilation.</param>
    private static bool Is(INamedTypeSymbol type, INamedTypeSymbol? known)
        => known is not null && SymbolEqualityComparer.Default.Equals(type, known);
}
