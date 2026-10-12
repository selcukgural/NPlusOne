using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NPlusOne.EntityFrameworkCore;

namespace NPlusOne.AspNetCore;

/// <summary>Shared logic of the MVC and endpoint assert filters.</summary>
internal static class RequestScope
{
    /// <summary>
    /// Begins a scope with the application's options, or the defaults when
    /// <see cref="NPlusOneServiceCollectionExtensions.AddNPlusOneGuard"/> was not called.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The new, current scope.</returns>
    public static NPlusOneScope Begin(HttpContext context)
    {
        var options = context.RequestServices.GetService<IOptions<NPlusOneOptions>>()?.Value ?? new NPlusOneOptions();
        return NPlusOneGuard.Begin(options);
    }

    /// <summary>Runs a filter's continuation inside a scope and throws when it leaves suspects.</summary>
    /// <param name="context">The request.</param>
    /// <param name="next">The continuation: the action or the endpoint handler.</param>
    /// <typeparam name="T">What the continuation returns.</typeparam>
    /// <returns>The continuation's result.</returns>
    /// <exception cref="NPlusOneViolationException">The continuation ran repeated queries or lazy loads.</exception>
    public static async Task<T> AssertAsync<T>(HttpContext context, Func<Task<T>> next)
    {
        T result;
        NPlusOneReport report;
        using (var scope = Begin(context))
        {
            result = await next();
            report = scope.Report;
        }

        return report.HasSuspects ? throw new NPlusOneViolationException(report) : result;
    }
}
