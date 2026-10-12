using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NPlusOne.EntityFrameworkCore.Recording;

namespace NPlusOne.EntityFrameworkCore;

/// <summary>Attaches the N+1 guard to a DbContext.</summary>
public static class NPlusOneDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Adds the guard's command interceptor and starts listening for lazy loads. The guard records
    /// nothing until a scope begins, and costs one async-local read per command outside a scope.
    /// Works in <c>OnConfiguring</c>, in <c>AddDbContext</c> and with a hand-built
    /// <see cref="DbContextOptionsBuilder"/>; calling it twice attaches the guard once.
    /// </summary>
    /// <param name="optionsBuilder">The builder of the DbContext options.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder UseNPlusOneGuard(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        LazyLoadListener.EnsureSubscribed();
        var interceptors = optionsBuilder.Options.FindExtension<CoreOptionsExtension>()?.Interceptors;
        if (interceptors is not null && interceptors.Contains(NPlusOneCommandInterceptor.Instance))
        {
            return optionsBuilder;
        }

        return optionsBuilder.AddInterceptors(NPlusOneCommandInterceptor.Instance);
    }

    /// <summary>
    /// Adds the guard to a typed options builder; see
    /// <see cref="UseNPlusOneGuard(DbContextOptionsBuilder)"/>.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type.</typeparam>
    /// <param name="optionsBuilder">The builder of the DbContext options.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseNPlusOneGuard<TContext>(this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
    {
        UseNPlusOneGuard((DbContextOptionsBuilder)optionsBuilder);
        return optionsBuilder;
    }
}
