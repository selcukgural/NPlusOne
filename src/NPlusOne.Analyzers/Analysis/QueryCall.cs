using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>How one invocation takes part in a query.</summary>
/// <param name="Kind">What the call does.</param>
/// <param name="Source">The query or sequence it composes on or runs (its <c>this</c> argument), when it has one.</param>
/// <param name="IncludePath">For <c>Include</c>/<c>ThenInclude</c>, the navigation path the call adds to the root's includes.</param>
/// <param name="Projects">Whether the call replaces the rows with new ones (<c>Select</c>, <c>GroupBy</c>), so includes stop applying.</param>
/// <param name="NotEfWhenSourceIsNotQuery">For <c>AsQueryable()</c>: over an in-memory sequence the result is LINQ to Objects.</param>
internal sealed record QueryCall(
    QueryCallKind Kind,
    IOperation? Source = null,
    string? IncludePath = null,
    bool Projects = false,
    bool NotEfWhenSourceIsNotQuery = false)
{
    /// <summary>A call that has nothing to do with queries.</summary>
    public static readonly QueryCall None = new(QueryCallKind.None);
}
