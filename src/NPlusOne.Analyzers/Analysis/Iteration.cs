using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>How code ends up running once per element.</summary>
internal enum IterationKind
{
    /// <summary>A <c>for</c>, <c>foreach</c>, <c>while</c> or <c>do</c> loop.</summary>
    Loop,

    /// <summary>A delegate called once per element (a LINQ lambda, <c>ForEach</c>, <c>Parallel.ForEach</c>).</summary>
    Callback,

    /// <summary>Inside an expression tree: translated into the outer SQL statement, never a round trip.</summary>
    ExpressionTree,
}

/// <summary>The innermost place that makes an operation run once per element.</summary>
/// <param name="Kind">Loop or callback.</param>
/// <param name="Region">The loop or lambda operation; diagnostics are deduplicated per region.</param>
/// <param name="Description">How messages name it: <c>the foreach loop over 'customers'</c>, <c>the 'Select' lambda</c>.</param>
/// <param name="IsBatched">Whether the loop walks batches (<c>Chunk</c>): one round trip per batch is the fix, not the problem.</param>
internal sealed record Iteration(IterationKind Kind, IOperation Region, string Description, bool IsBatched)
{
    /// <summary>The marker for operations inside an expression tree.</summary>
    public static readonly Iteration InExpressionTree = new(IterationKind.ExpressionTree, null!, "", false);
}
