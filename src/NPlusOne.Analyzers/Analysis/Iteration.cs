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

/// <summary>
/// The innermost place that makes an operation run once per element. Its description is for messages only, and most
/// iterations are found for calls that report nothing, so it can be built on first use from the region.
/// </summary>
internal sealed class Iteration
{
    /// <summary>The marker for operations inside an expression tree.</summary>
    public static readonly Iteration InExpressionTree = new(IterationKind.ExpressionTree, null!, "", false);

    /// <summary>Builds the description from the region; null once <see cref="_description"/> is set.</summary>
    private readonly Func<IOperation, string>? _describe;

    /// <summary>The description, once built.</summary>
    private string? _description;

    /// <summary>Creates an iteration with its description.</summary>
    /// <param name="kind">Loop or callback.</param>
    /// <param name="region">The loop or lambda operation; diagnostics are deduplicated per region.</param>
    /// <param name="description">How messages name it: <c>the foreach loop over 'customers'</c>, <c>the 'Select' lambda</c>.</param>
    /// <param name="isBatched">Whether the loop walks batches (<c>Chunk</c>): one round trip per batch is the fix, not the problem.</param>
    public Iteration(IterationKind kind, IOperation region, string description, bool isBatched)
    {
        Kind = kind;
        Region = region;
        _description = description;
        IsBatched = isBatched;
    }

    /// <summary>Creates an iteration whose description is built when a message first needs it.</summary>
    /// <param name="kind">Loop or callback.</param>
    /// <param name="region">The loop or lambda operation.</param>
    /// <param name="describe">Builds the description from the region; a static lambda, so creating the iteration captures nothing.</param>
    /// <param name="isBatched">Whether the loop walks batches.</param>
    public Iteration(IterationKind kind, IOperation region, Func<IOperation, string> describe, bool isBatched)
    {
        Kind = kind;
        Region = region;
        _describe = describe;
        IsBatched = isBatched;
    }

    /// <summary>Loop or callback.</summary>
    public IterationKind Kind { get; }

    /// <summary>The loop or lambda operation; diagnostics are deduplicated per region.</summary>
    public IOperation Region { get; }

    /// <summary>How messages name it: <c>the foreach loop over 'customers'</c>, <c>the 'Select' lambda</c>.</summary>
    public string Description => _description ??= _describe!(Region);

    /// <summary>Whether the loop walks batches (<c>Chunk</c>): one round trip per batch is the fix, not the problem.</summary>
    public bool IsBatched { get; }
}
