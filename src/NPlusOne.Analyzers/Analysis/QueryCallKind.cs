namespace NPlusOne.Analyzers.Analysis;

/// <summary>What a method call does to a query.</summary>
internal enum QueryCallKind
{
    /// <summary>Nothing query-related.</summary>
    None,

    /// <summary>Starts an EF query: <c>Set&lt;T&gt;()</c>, <c>Database.SqlQuery</c>, <c>Entry(e).Collection(...).Query()</c>.</summary>
    Root,

    /// <summary>Returns a new query or deferred sequence over its source (<c>Where</c>, <c>Include</c>, <c>AsEnumerable</c>).</summary>
    Compose,

    /// <summary>Runs its source when the source is still a query (<c>ToList</c>, <c>Count</c>, <c>ToListAsync</c>).</summary>
    Execute,

    /// <summary>Always runs SQL, whatever its arguments (<c>Find</c>, explicit <c>Load</c>, <c>ExecuteSql</c>).</summary>
    ExecuteAlways,

    /// <summary><c>SaveChanges</c>: writes the tracked changes in one round trip.</summary>
    Save,
}
