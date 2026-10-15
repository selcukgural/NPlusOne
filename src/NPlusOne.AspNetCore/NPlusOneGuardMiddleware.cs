using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NPlusOne.EntityFrameworkCore;

namespace NPlusOne.AspNetCore;

/// <summary>
/// Opens one guard scope per request and reports its suspects when the request ends.
/// </summary>
/// <remarks>
/// A violation can only become an exception while the response has not started: once headers and
/// part of the body are on the wire, an exception cannot turn the response into an error, the
/// server can only cut the connection and the client sees a truncated body. Most endpoints that
/// return data have started their response by the time the pipeline returns, so in that case the
/// middleware logs an error instead. To fail a request reliably, use <see cref="NPlusOneAssertAttribute"/>
/// or <see cref="NPlusOneEndpointConventionBuilderExtensions.WithNPlusOneAssert{TBuilder}"/>, which
/// check before the result is written.
/// </remarks>
/// <param name="next">The rest of the pipeline.</param>
/// <param name="options">The guard options.</param>
/// <param name="logger">Receives the reports.</param>
internal sealed class NPlusOneGuardMiddleware(RequestDelegate next, IOptions<NPlusOneOptions> options, ILogger<NPlusOneGuardMiddleware> logger)
{
    /// <summary>Runs the request inside a scope, then logs or throws when it has suspects.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        // A WebSocket (a SignalR hub, a Blazor Server circuit) keeps one request open for a whole session: one scope
        // would add up every click into one "request" and report the same query run on separate actions as N+1.
        if (context.WebSockets.IsWebSocketRequest)
        {
            await next(context);
            return;
        }

        var settings = options.Value;
        NPlusOneReport report;
        using (var scope = NPlusOneGuard.Begin(settings))
        {
            // If the pipeline throws, its exception propagates unchanged: it is the more important error.
            await next(context);
            report = scope.Report;
        }

        if (!report.HasSuspects)
        {
            return;
        }

        var endpoint = RequestScope.EndpointName(context);
        if (!settings.ThrowOnViolation)
        {
            NPlusOneLog.SuspectsFound(logger, context.Request.Method, endpoint, report);
            return;
        }

        if (context.Response.HasStarted)
        {
            NPlusOneLog.ViolationAfterResponseStarted(logger, context.Request.Method, endpoint, report);
            return;
        }

        throw new NPlusOneViolationException(report);
    }
}
