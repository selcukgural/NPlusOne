using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace NPlusOne.EntityFrameworkCore.Recording;

/// <summary>
/// What the guard needs to know about a context on every command and lazy load, found once per context instance: its
/// SQL dialect and whether the guard is attached to it. Reading them resolves services (<c>Database.ProviderName</c>
/// enumerates the providers, the options extension is looked up), which would otherwise happen on every command.
/// </summary>
/// <remarks>
/// Kept in a <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed by the context, so a context that is disposed and
/// collected takes its entry with it; a pooled context keeps its provider and options, so its entry stays right.
/// </remarks>
internal sealed class ContextFacts
{
    /// <summary>The facts of each context seen, released with the context.</summary>
    private static readonly ConditionalWeakTable<DbContext, ContextFacts> Known = [];

    /// <summary>Builds the facts of a context; kept in a field so a lookup allocates no delegate.</summary>
    private static readonly ConditionalWeakTable<DbContext, ContextFacts>.CreateValueCallback Build = static context => new ContextFacts(context);

    /// <summary>The facts when EF Core gives no context: an unknown dialect, which hides the most, and no guard known.</summary>
    private static readonly ContextFacts None = new(SqlDialect.Unknown, false);

    /// <summary>Reads the facts of a context.</summary>
    /// <param name="context">The context.</param>
    private ContextFacts(DbContext context)
        : this(SqlFingerprint.DialectOf(context.Database.ProviderName), HasGuard(context))
    {
    }

    /// <summary>Creates facts from their values.</summary>
    /// <param name="dialect">The dialect of the context's provider.</param>
    /// <param name="usesGuard">Whether the context's options carry the guard's interceptor.</param>
    private ContextFacts(SqlDialect dialect, bool usesGuard)
    {
        Dialect = dialect;
        UsesGuard = usesGuard;
    }

    /// <summary>The dialect of the context's provider, which decides how literals are found in its commands.</summary>
    public SqlDialect Dialect { get; }

    /// <summary>Whether the context was built with <c>UseNPlusOneGuard</c>.</summary>
    public bool UsesGuard { get; }

    /// <summary>The facts of a context, read on first use.</summary>
    /// <param name="context">The context, or <see langword="null"/> when EF Core gives none.</param>
    public static ContextFacts Of(DbContext? context) => context is null ? None : Known.GetValue(context, Build);

    /// <summary>Whether a context's options carry the guard's interceptor.</summary>
    /// <param name="context">The context.</param>
    private static bool HasGuard(DbContext context)
        => context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors?.Contains(NPlusOneCommandInterceptor.Instance) == true;
}
