namespace NPlusOne.EntityFrameworkCore;

/// <summary>
/// What a suspect group counts: a read query, a write command or a lazy load of a navigation.
/// </summary>
public enum NPlusOneSuspectKind
{
    /// <summary>A read command (a LINQ query or <c>FromSql</c>) that ran once per element.</summary>
    Query,

    /// <summary>
    /// A write command (<c>SaveChanges</c>, <c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, <c>ExecuteSql</c>)
    /// that ran once per element. Recorded only when <see cref="NPlusOneOptions.IncludeWrites"/> is on.
    /// </summary>
    Write,

    /// <summary>
    /// A lazy load of one navigation that ran once per element. Its SQL is recorded as a
    /// <see cref="Query"/> too; this kind names the navigation so the fix is obvious.
    /// </summary>
    LazyLoad,
}
