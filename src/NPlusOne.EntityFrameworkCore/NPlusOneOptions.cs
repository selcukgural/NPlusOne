using System.Text.RegularExpressions;

namespace NPlusOne.EntityFrameworkCore;

/// <summary>
/// Settings for a guard scope. A scope copies them when it begins, so changing an instance later
/// does not affect scopes that are already running.
/// </summary>
public sealed class NPlusOneOptions
{
    /// <summary>
    /// The number of runs a group (same normalized SQL, or same lazy-loaded navigation, from the same
    /// call site) may reach in one scope. A group that runs more often is an N+1 suspect. Default 2:
    /// the third identical query from the same line is reported. Must be at least 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int RepeatThreshold
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 2;

    /// <summary>
    /// Whether a host that ends the scope for you (the ASP.NET Core middleware) throws
    /// <see cref="NPlusOneViolationException"/> when the scope has suspects, instead of logging a
    /// warning. Manual scopes ignore it: call <see cref="NPlusOneScope.AssertNoNPlusOne"/> to throw.
    /// Default <see langword="false"/>.
    /// </summary>
    public bool ThrowOnViolation { get; set; }

    /// <summary>
    /// Commands whose SQL text matches any of these patterns are not recorded. The patterns run
    /// against the SQL as EF Core sends it, so a pattern can name a table or a tag.
    /// </summary>
    public IList<Regex> IgnoredSqlPatterns { get; } = [];

    /// <summary>
    /// Whether write commands (<c>SaveChanges</c>, <c>ExecuteUpdate</c>, <c>ExecuteDelete</c>,
    /// <c>ExecuteSql</c>) are recorded, so a <c>SaveChanges</c> call per element is reported.
    /// Default <see langword="false"/>: only reads are recorded.
    /// </summary>
    public bool IncludeWrites { get; set; }

    /// <summary>
    /// Whether each command and lazy load records the first stack frame outside EF Core, the .NET
    /// libraries and the guard. Groups then include the call site, so the same SQL issued from two
    /// places is two groups, and reports say where to look. Capturing a stack costs a few
    /// microseconds per command while a scope is active. Default <see langword="true"/>.
    /// </summary>
    public bool CaptureCallSite { get; set; } = true;

    /// <summary>
    /// The maximum length of the normalized SQL shown for a suspect; longer text is cut and ends with
    /// "...". Must be at least 20. Default 300.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 20.</exception>
    public int MaxSqlPreviewLength
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 20);
            field = value;
        }
    } = 300;
}
