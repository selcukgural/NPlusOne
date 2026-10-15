using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NPlusOne.EntityFrameworkCore;

namespace NPlusOne.AspNetCore;

/// <summary>Adds the N+1 assert filter to minimal API endpoints.</summary>
public static class NPlusOneEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Fails the endpoint (or every endpoint of a route group) with <see cref="NPlusOneViolationException"/>
    /// when its handler runs a query or a lazy load once per element. The check runs right after the
    /// handler and before its result is written, like <see cref="NPlusOneAssertAttribute"/> for MVC, and in the
    /// Production environment logs a warning instead unless <see cref="NPlusOneOptions.AssertThrowsInProduction"/> is set.
    /// Applies to route handlers (<c>MapGet(pattern, delegate)</c> and friends), which support
    /// endpoint filters.
    /// </summary>
    /// <param name="builder">The endpoint or route group builder.</param>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder WithNPlusOneAssert<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(static (context, next) =>
            new ValueTask<object?>(RequestScope.AssertAsync(context.HttpContext, () => next(context).AsTask())));
    }
}
