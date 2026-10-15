using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    public static NPlusOneScope Begin(HttpContext context) => NPlusOneGuard.Begin(Options(context));

    /// <summary>Runs a filter's continuation inside a scope and checks it when it returns.</summary>
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

        Check(context, report);
        return result;
    }

    /// <summary>
    /// Fails the request when the report has suspects, or, in the Production environment without
    /// <see cref="NPlusOneOptions.AssertThrowsInProduction"/>, logs the report as a warning: there an assert would let
    /// the data decide whether the endpoint works.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="report">The report of the filter's scope.</param>
    /// <exception cref="NPlusOneViolationException">The report has suspects and the assert throws here.</exception>
    public static void Check(HttpContext context, NPlusOneReport report)
    {
        if (!report.HasSuspects)
        {
            return;
        }

        var services = context.RequestServices;
        if (Options(context).AssertThrowsInProduction || services.GetService<IHostEnvironment>() is not { } environment || !environment.IsProduction())
        {
            throw new NPlusOneViolationException(report);
        }

        if (services.GetService<ILogger<NPlusOneAssertAttribute>>() is { } logger)
        {
            NPlusOneLog.AssertInProduction(logger, context.Request.Method, EndpointName(context), report);
        }
    }

    /// <summary>
    /// Names the endpoint by its display name (route template or action), never by the request path,
    /// so identifiers and other values in the URL do not reach the log.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The endpoint name, or a placeholder when no endpoint matched.</returns>
    public static string EndpointName(HttpContext context) => context.GetEndpoint()?.DisplayName ?? "(no endpoint)";

    /// <summary>The application's options, or the defaults when none are registered.</summary>
    /// <param name="context">The request.</param>
    private static NPlusOneOptions Options(HttpContext context)
        => context.RequestServices.GetService<IOptions<NPlusOneOptions>>()?.Value ?? new NPlusOneOptions();
}
