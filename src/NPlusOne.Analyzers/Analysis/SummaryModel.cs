using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>What a database effect does.</summary>
internal enum EffectKind
{
    /// <summary>Runs a query (a read, explicit loading, raw SQL, a set-based update).</summary>
    Query,

    /// <summary>Runs <c>SaveChanges</c>.</summary>
    Save,
}

/// <summary>A database round trip a method makes directly, in its own body.</summary>
/// <param name="Kind">What the round trip does.</param>
/// <param name="Source">Ef when it always runs; Parameter when it runs only if the caller passes an EF query.</param>
/// <param name="Name">The operator that runs it, such as <c>ToListAsync</c>.</param>
/// <param name="Location">Where it is.</param>
/// <param name="Shape">What the query looks like, for the suggested rewrite.</param>
internal sealed record Effect(EffectKind Kind, SourceInfo Source, string Name, Location Location, QueryShape Shape);

/// <summary>A call a method makes to another method whose summary may add round trips.</summary>
/// <param name="Callee">The method called.</param>
/// <param name="Arguments">What each argument is, by the callee's parameter ordinal.</param>
/// <param name="Location">Where the call is.</param>
/// <param name="Dispatched">Whether the call can run an override or implementation; a base call runs only the callee's own body.</param>
internal sealed record CallEdge(IMethodSymbol Callee, ImmutableArray<SourceInfo> Arguments, Location Location, bool Dispatched);

/// <summary>
/// A lazy navigation a method reads on one of its parameters: on the parameter itself (a mapper reading
/// <c>order.Customer</c>), on each element of a sequence parameter it walks (<c>foreach (var o in orders)
/// o.Customer</c>), or on <c>this</c> (an entity's computed property reading <c>Lines</c>).
/// </summary>
/// <param name="Parameter">The parameter's ordinal; <see cref="This"/> for the instance.</param>
/// <param name="Path">The navigation path read, such as <c>Customer</c>.</param>
/// <param name="PerElement">Whether it is read on every element of the parameter rather than on the parameter.</param>
internal sealed record ParameterNavigationRead(int Parameter, string Path, bool PerElement)
{
    /// <summary>The ordinal that stands for <c>this</c>.</summary>
    public const int This = -1;
}

/// <summary>
/// What one method does that matters to callers, computed once per compilation from its body.
/// Transitive facts (what its callees do) are resolved on demand by <see cref="Session"/>.
/// </summary>
/// <param name="Effects">Round trips made directly in the body.</param>
/// <param name="Calls">Calls to methods that may make round trips.</param>
/// <param name="DelegatesInvokedPerElement">Ordinals of delegate parameters the method calls once per element.</param>
/// <param name="NavigationReads">Lazy navigations read on parameters.</param>
/// <param name="Returns">What the method returns, when it returns a query, a sequence or an entity.</param>
internal sealed record MethodSummary(
    ImmutableArray<Effect> Effects,
    ImmutableArray<CallEdge> Calls,
    ImmutableHashSet<int> DelegatesInvokedPerElement,
    ImmutableArray<ParameterNavigationRead> NavigationReads,
    SourceInfo Returns)
{
    /// <summary>The summary of a method whose body is not available or is being built.</summary>
    public static readonly MethodSummary Empty = new(
        ImmutableArray<Effect>.Empty,
        ImmutableArray<CallEdge>.Empty,
        ImmutableHashSet<int>.Empty,
        ImmutableArray<ParameterNavigationRead>.Empty,
        SourceInfo.Unknown);
}
