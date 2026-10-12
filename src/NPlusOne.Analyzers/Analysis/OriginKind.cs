namespace NPlusOne.Analyzers.Analysis;

/// <summary>Where a value that may be a query comes from.</summary>
internal enum OriginKind
{
    /// <summary>Not known: a field, an unresolved call, a mix of sources.</summary>
    Unknown,

    /// <summary>From EF Core: a <c>DbSet</c>, <c>Set&lt;T&gt;()</c>, raw SQL, or a method that returns one.</summary>
    Ef,

    /// <summary>Provably not EF Core: an in-memory sequence, <c>AsQueryable()</c> over a list.</summary>
    NotEf,

    /// <summary>A parameter of the method being summarised; the caller's argument decides.</summary>
    Parameter,

    /// <summary>A local still being traced (a self-referencing assignment); it does not change the result.</summary>
    Pending,
}
