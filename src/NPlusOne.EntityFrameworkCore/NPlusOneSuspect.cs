using System.Globalization;

namespace NPlusOne.EntityFrameworkCore;

/// <summary>
/// One group of identical work that ran more than <see cref="NPlusOneOptions.RepeatThreshold"/> times
/// in a scope: the same normalized SQL, or the same lazy-loaded navigation, from the same call site.
/// </summary>
public sealed class NPlusOneSuspect
{
    /// <summary>Creates a suspect; only the guard creates them.</summary>
    /// <param name="kind">What the group counts.</param>
    /// <param name="count">How many times the group ran.</param>
    /// <param name="sql">The normalized SQL preview, for queries and writes.</param>
    /// <param name="navigation">The <c>Entity.Navigation</c> name, for lazy loads.</param>
    /// <param name="callSite">Where the work was issued from, when known.</param>
    internal NPlusOneSuspect(NPlusOneSuspectKind kind, int count, string? sql, string? navigation, string? callSite)
    {
        Kind = kind;
        Count = count;
        Sql = sql;
        Navigation = navigation;
        CallSite = callSite;
    }

    /// <summary>What the group counts.</summary>
    public NPlusOneSuspectKind Kind { get; }

    /// <summary>How many times the group ran in the scope.</summary>
    public int Count { get; }

    /// <summary>
    /// The normalized SQL (parameters, literals and comments replaced, cut to
    /// <see cref="NPlusOneOptions.MaxSqlPreviewLength"/>) for queries and writes;
    /// <see langword="null"/> for lazy loads. Never contains parameter values or literals.
    /// </summary>
    public string? Sql { get; }

    /// <summary>
    /// The lazy-loaded navigation as <c>Entity.Navigation</c> (for example <c>Order.Customer</c>);
    /// <see langword="null"/> for queries and writes.
    /// </summary>
    public string? Navigation { get; }

    /// <summary>
    /// The first stack frame outside EF Core, the .NET libraries and the guard, as
    /// <c>Type.Method (File.cs:line)</c> when symbols are available. <see langword="null"/> when call
    /// sites are not captured or no such frame was on the stack (for example after an asynchronous
    /// connection open resumed on another thread).
    /// </summary>
    public string? CallSite { get; }

    /// <summary>Formats the suspect as one line, followed by the SQL on a second line for queries and writes.</summary>
    /// <returns>A readable description without parameter values.</returns>
    public override string ToString()
    {
        var what = Kind switch
        {
            NPlusOneSuspectKind.LazyLoad => $"Lazy load of {Navigation}",
            NPlusOneSuspectKind.Write => "Write command",
            _ => "Query",
        };
        var where = CallSite is null ? string.Empty : $" at {CallSite}";
        var head = string.Create(CultureInfo.InvariantCulture, $"{what} ran {Count} times{where}");
        return Sql is null ? head : $"{head}{Environment.NewLine}    {Sql}";
    }
}
