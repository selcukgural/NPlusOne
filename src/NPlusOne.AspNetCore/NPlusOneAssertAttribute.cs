using Microsoft.AspNetCore.Mvc.Filters;
using NPlusOne.EntityFrameworkCore;

namespace NPlusOne.AspNetCore;

/// <summary>
/// Fails an MVC action (or every action of a controller) with <see cref="NPlusOneViolationException"/>
/// when it runs a query or a lazy load once per element. The check runs right after the action and
/// before its result is written, so the exception still becomes an error response. Meant for
/// integration tests and development; it throws regardless of <see cref="NPlusOneOptions.ThrowOnViolation"/>, except in
/// the Production environment, where it logs a warning unless <see cref="NPlusOneOptions.AssertThrowsInProduction"/>
/// is set. It uses the other options registered with <see cref="NPlusOneServiceCollectionExtensions.AddNPlusOneGuard"/>.
/// </summary>
/// <remarks>
/// Work done while the result is written (for example lazy loads triggered by the JSON serializer)
/// happens after the check; the middleware's request scope sees it.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class NPlusOneAssertAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>Runs the action inside its own scope and throws (or, in Production, logs) if the scope has suspects.</summary>
    /// <param name="context">The action context.</param>
    /// <param name="next">Runs the action.</param>
    /// <returns>A task that completes after the action and the check.</returns>
    /// <exception cref="NPlusOneViolationException">The action ran repeated queries or lazy loads, outside Production.</exception>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        ActionExecutedContext executed;
        NPlusOneReport report;
        using (var scope = RequestScope.Begin(context.HttpContext))
        {
            executed = await next();
            report = scope.Report;
        }

        // An action that failed reports its own exception; a violation on top would hide it.
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return;
        }

        RequestScope.Check(context.HttpContext, report);
    }
}
