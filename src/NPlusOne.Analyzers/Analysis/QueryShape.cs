namespace NPlusOne.Analyzers.Analysis;

/// <summary>The kind of round trip, which decides the rewrite a message suggests.</summary>
internal enum ShapeKind
{
    /// <summary>A query that returns rows.</summary>
    Rows,

    /// <summary>An aggregate (<c>Count</c>, <c>Any</c>, <c>Sum</c>, ...).</summary>
    Aggregate,

    /// <summary><c>Find</c> by primary key.</summary>
    Find,

    /// <summary>Explicit loading of a navigation.</summary>
    ExplicitLoad,

    /// <summary>Raw SQL or a set-based update or delete.</summary>
    Statement,

    /// <summary><c>SaveChanges</c>.</summary>
    Save,

    /// <summary>A method declared to run a query in the options; its body is not known.</summary>
    Declared,
}

/// <summary>
/// What a query looks like: its kind, the key it filters on (<c>o =&gt; o.CustomerId == x</c> gives
/// <c>o</c> and <c>CustomerId</c>), and whether it pages (<c>OrderBy</c> plus <c>Take</c>).
/// </summary>
/// <param name="Kind">The kind of round trip.</param>
/// <param name="Operator">The executing operator, such as <c>CountAsync</c>.</param>
/// <param name="KeyParameter">The lambda parameter name of the key filter, when there is one.</param>
/// <param name="KeyMember">The member compared to a per-element value, when there is one.</param>
/// <param name="IsPaged">Whether the query is ordered and bounded with <c>Take</c>, as paging and keyset loops are.</param>
/// <param name="Selector">The source text of an aggregate's value selector (<c>o =&gt; o.Total</c> in <c>SumAsync(o =&gt; o.Total)</c>).</param>
internal sealed record QueryShape(ShapeKind Kind, string Operator, string? KeyParameter, string? KeyMember, bool IsPaged, string? Selector = null)
{
    /// <summary>The shape of a method declared to run a query.</summary>
    /// <param name="name">The method's name.</param>
    public static QueryShape Declared(string name) => new(ShapeKind.Declared, name, null, null, false);
}
