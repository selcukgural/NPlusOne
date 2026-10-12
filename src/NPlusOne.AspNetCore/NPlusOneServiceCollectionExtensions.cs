using Microsoft.Extensions.DependencyInjection;
using NPlusOne.EntityFrameworkCore;

namespace NPlusOne.AspNetCore;

/// <summary>Registers the N+1 guard's options for the middleware and the assert filters.</summary>
public static class NPlusOneServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="NPlusOneOptions"/> with the options system. The middleware
    /// (<see cref="NPlusOneApplicationBuilderExtensions.UseNPlusOneGuard"/>), <see cref="NPlusOneAssertAttribute"/>
    /// and <see cref="NPlusOneEndpointConventionBuilderExtensions.WithNPlusOneAssert{TBuilder}"/> read them.
    /// Attach the guard to each DbContext with <c>options.UseNPlusOneGuard()</c> inside <c>AddDbContext</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Changes the default options; may be <see langword="null"/>.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddNPlusOneGuard(this IServiceCollection services, Action<NPlusOneOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = services.AddOptions<NPlusOneOptions>();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        // A bad option fails the application at startup, not every request at its first query.
        builder.Validate(static o => o.IgnoredSqlPatterns.All(static p => p is not null), "NPlusOneOptions.IgnoredSqlPatterns contains null.")
            .ValidateOnStart();
        return services;
    }
}
