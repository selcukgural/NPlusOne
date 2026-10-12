using NPlusOne.EntityFrameworkCore.Recording;

namespace NPlusOne.EntityFrameworkCore;

/// <summary>
/// Entry point for manual scopes, usable from any test framework without dependency injection:
/// <code>
/// using var guard = NPlusOneGuard.Begin();
/// // code under test
/// guard.AssertNoNPlusOne();
/// </code>
/// The DbContext must have the guard attached with
/// <see cref="NPlusOneDbContextOptionsBuilderExtensions.UseNPlusOneGuard(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder)"/>.
/// </summary>
public static class NPlusOneGuard
{
    /// <summary>Begins a scope with the default options and makes it current for this async flow.</summary>
    /// <returns>The scope; dispose it to stop recording.</returns>
    public static NPlusOneScope Begin() => Begin(new NPlusOneOptions());

    /// <summary>Begins a scope with options adjusted by <paramref name="configure"/>.</summary>
    /// <param name="configure">Changes the default options, for example <c>o =&gt; o.RepeatThreshold = 3</c>.</param>
    /// <returns>The scope; dispose it to stop recording.</returns>
    public static NPlusOneScope Begin(Action<NPlusOneOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new NPlusOneOptions();
        configure(options);
        return Begin(options);
    }

    /// <summary>Begins a scope with a copy of <paramref name="options"/>.</summary>
    /// <param name="options">The options; later changes to the instance do not affect the scope.</param>
    /// <returns>The scope; dispose it to stop recording.</returns>
    /// <exception cref="ArgumentException"><see cref="NPlusOneOptions.IgnoredSqlPatterns"/> contains <see langword="null"/>.</exception>
    public static NPlusOneScope Begin(NPlusOneOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = ScopeSettings.From(options);
        LazyLoadListener.EnsureSubscribed();
        return NPlusOneScope.Start(settings);
    }
}
