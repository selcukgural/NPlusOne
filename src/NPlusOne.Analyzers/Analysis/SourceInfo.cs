using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// What tracing an expression found: whether it is EF-backed, whether it is still an unexecuted
/// query (enumerating it runs SQL) or already materialised, and, for entities, which navigations the
/// query loaded.
/// </summary>
/// <param name="Kind">Where the value comes from.</param>
/// <param name="Parameter">The parameter ordinal when <paramref name="Kind"/> is <see cref="OriginKind.Parameter"/>.</param>
/// <param name="IsQuery">True for a query that has not run yet; false for materialised results.</param>
/// <param name="Includes">
/// The navigation paths the query includes, prefix-closed and relative to the query's root entity
/// (<c>Customer</c>, <c>Customer.Address</c>).
/// </param>
/// <param name="Path">
/// The navigation path of these elements from the root entity: empty for the query's own rows,
/// <c>Orders</c> for the elements of <c>customer.Orders</c>.
/// </param>
/// <param name="Root">
/// Where the query starts (<c>db.Orders</c>, <c>db.Set&lt;Order&gt;()</c>), so a code fix can add an
/// <c>Include</c> there; null when it is unknown or the rows were projected since.
/// </param>
/// <param name="Untracked">
/// Whether the query does not track its results (<c>AsNoTracking</c>): only the navigations that load for
/// untracked entities (<see cref="Model.Navigation.IsLazyWhenUntracked"/>, none before EF Core 8) are round trips.
/// </param>
/// <param name="IgnoresAutoIncludes">Whether the query calls <c>IgnoreAutoIncludes()</c>, so auto-included navigations are not loaded.</param>
/// <param name="IsElement">
/// Whether this is one element of a sequence (a <c>foreach</c> variable, a per-element lambda's
/// parameter), as opposed to the sequence or a single result such as <c>First()</c>.
/// </param>
internal sealed record SourceInfo(
    OriginKind Kind,
    int Parameter,
    bool IsQuery,
    ImmutableHashSet<string> Includes,
    string Path,
    Location? Root = null,
    bool Untracked = false,
    bool IgnoresAutoIncludes = false,
    bool IsElement = false)
{
    /// <summary>Nothing is known.</summary>
    public static readonly SourceInfo Unknown = new(OriginKind.Unknown, -1, false, ImmutableHashSet<string>.Empty, "");

    /// <summary>Provably in memory.</summary>
    public static readonly SourceInfo NotEf = new(OriginKind.NotEf, -1, false, ImmutableHashSet<string>.Empty, "");

    /// <summary>A local whose tracing is in progress.</summary>
    public static readonly SourceInfo Pending = new(OriginKind.Pending, -1, false, ImmutableHashSet<string>.Empty, "");

    /// <summary>A fresh EF query over an entity set, with nothing included.</summary>
    public static readonly SourceInfo EfQuery = new(OriginKind.Ef, -1, true, ImmutableHashSet<string>.Empty, "");

    /// <summary>
    /// Stands in <see cref="Includes"/> for navigations loaded by a configuration the analysis cannot read (ABP's
    /// <c>WithDetails()</c> and <c>includeDetails: true</c> load what the application configured): every navigation
    /// counts as loaded, so no lazy load is reported where one may not happen.
    /// </summary>
    public const string AnyInclude = "*";

    /// <summary>Whether this is an EF query that has not run yet: enumerating it or calling an executing operator runs SQL.</summary>
    public bool IsEfQuery => Kind == OriginKind.Ef && IsQuery;

    /// <summary>A parameter of the method being summarised.</summary>
    /// <param name="ordinal">The parameter's ordinal.</param>
    public static SourceInfo FromParameter(int ordinal) => new(OriginKind.Parameter, ordinal, true, ImmutableHashSet<string>.Empty, "");

    /// <summary>One element of this sequence.</summary>
    public SourceInfo Element() => Materialized() with { IsElement = true };

    /// <summary>The same source, materialised: the query ran and these are its results.</summary>
    public SourceInfo Materialized() => IsQuery ? this with { IsQuery = false } : this;

    /// <summary>The same source with one more included navigation path (and all its prefixes).</summary>
    /// <param name="path">A dotted navigation path such as <c>Lines.Product</c>.</param>
    public SourceInfo WithInclude(string path)
    {
        if (Kind != OriginKind.Ef || path.Length == 0)
        {
            return this;
        }

        var builder = Includes.ToBuilder();
        var dot = path.IndexOf('.');
        while (dot > 0)
        {
            builder.Add(path.Substring(0, dot));
            dot = path.IndexOf('.', dot + 1);
        }

        builder.Add(path);
        return this with { Includes = builder.ToImmutable() };
    }

    /// <summary>
    /// Whether the navigation at <paramref name="path"/> was loaded by the query: it is included, or the
    /// model auto-includes it and the query does not ignore auto-includes.
    /// </summary>
    /// <param name="path">The navigation's full path from the query's root entity.</param>
    /// <param name="navigation">The navigation.</param>
    public bool Loads(string path, Model.Navigation navigation)
        => Includes.Contains(path) || Includes.Contains(AnyInclude) || (navigation.IsAutoIncluded && !IgnoresAutoIncludes);

    /// <summary>The same source with navigations loaded that the analysis cannot name (<see cref="AnyInclude"/>).</summary>
    public SourceInfo WithUnknownIncludes() => Kind == OriginKind.Ef ? this with { Includes = Includes.Add(AnyInclude) } : this;

    /// <summary>A projection (<c>Select</c>, <c>GroupBy</c>, ...) makes new rows: includes no longer apply.</summary>
    public SourceInfo Projected() => this with { Includes = ImmutableHashSet<string>.Empty, Path = "", Root = null };

    /// <summary>A fresh EF query that starts at the given expression.</summary>
    /// <param name="root">The <c>DbSet</c> or <c>Set&lt;T&gt;()</c> expression.</param>
    public static SourceInfo EfQueryAt(IOperation root) => EfQuery with { Root = root.Syntax.GetLocation() };

    /// <summary>
    /// Combines the sources a local was assigned from. Kinds must agree to stay known; includes
    /// are united, so a navigation included on any path counts as loaded and never causes a false
    /// lazy-load warning.
    /// </summary>
    /// <param name="other">The other source.</param>
    public SourceInfo Combine(SourceInfo other)
    {
        if (Kind == OriginKind.Pending)
        {
            return other;
        }

        if (other.Kind == OriginKind.Pending)
        {
            return this;
        }

        if (Kind != other.Kind || (Kind == OriginKind.Parameter && Parameter != other.Parameter))
        {
            return Unknown;
        }

        // Like includes, the flags lean towards "loaded": a lazy load is reported only when every
        // assignment makes it one.
        return this with
        {
            IsQuery = IsQuery || other.IsQuery,
            Includes = Includes.Union(other.Includes),
            Root = Equals(Root, other.Root) ? Root : null,
            Untracked = Untracked || other.Untracked,
            IgnoresAutoIncludes = IgnoresAutoIncludes && other.IgnoresAutoIncludes,
            IsElement = IsElement && other.IsElement,
        };
    }
}
