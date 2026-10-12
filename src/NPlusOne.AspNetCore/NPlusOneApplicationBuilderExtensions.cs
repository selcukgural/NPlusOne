using Microsoft.AspNetCore.Builder;

namespace NPlusOne.AspNetCore;

/// <summary>Adds the N+1 guard middleware to the request pipeline.</summary>
public static class NPlusOneApplicationBuilderExtensions
{
    /// <summary>
    /// Runs every later middleware and endpoint of a request inside one guard scope. When the
    /// request ends with suspects, the middleware logs a warning, or, with
    /// <see cref="EntityFrameworkCore.NPlusOneOptions.ThrowOnViolation"/>, throws
    /// <see cref="EntityFrameworkCore.NPlusOneViolationException"/> if the response has not started
    /// yet and logs an error if it has. Place it early, before the middleware whose queries it should see.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IApplicationBuilder UseNPlusOneGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<NPlusOneGuardMiddleware>();
    }
}
