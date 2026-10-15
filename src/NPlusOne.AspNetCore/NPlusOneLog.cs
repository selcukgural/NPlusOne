using Microsoft.Extensions.Logging;
using NPlusOne.EntityFrameworkCore;

namespace NPlusOne.AspNetCore;

/// <summary>Source-generated log messages of the guard middleware.</summary>
internal static partial class NPlusOneLog
{
    /// <summary>A request ended with suspects and the guard only reports them.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="endpoint">The endpoint display name.</param>
    /// <param name="report">The report; formatted only when the level is enabled.</param>
    [LoggerMessage(EventId = 1, EventName = "NPlusOneSuspects", Level = LogLevel.Warning, Message = "{Method} {Endpoint}: {Report}")]
    public static partial void SuspectsFound(ILogger logger, string method, string endpoint, NPlusOneReport report);

    /// <summary>
    /// A request ended with suspects, the guard should throw, but the response had already started,
    /// so throwing would only cut the connection.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="endpoint">The endpoint display name.</param>
    /// <param name="report">The report; formatted only when the level is enabled.</param>
    [LoggerMessage(
        EventId = 2,
        EventName = "NPlusOneViolationAfterResponseStarted",
        Level = LogLevel.Error,
        Message = "{Method} {Endpoint}: the response had already started, so the N+1 violation is logged instead of thrown. {Report}")]
    public static partial void ViolationAfterResponseStarted(ILogger logger, string method, string endpoint, NPlusOneReport report);

    /// <summary>
    /// An assert filter found suspects in the Production environment, where it logs instead of throwing unless
    /// <see cref="NPlusOneOptions.AssertThrowsInProduction"/> is set.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="endpoint">The endpoint display name.</param>
    /// <param name="report">The report; formatted only when the level is enabled.</param>
    [LoggerMessage(
        EventId = 3,
        EventName = "NPlusOneAssertInProduction",
        Level = LogLevel.Warning,
        Message = "{Method} {Endpoint}: an N+1 assert found suspects; in Production it logs instead of throwing. {Report}")]
    public static partial void AssertInProduction(ILogger logger, string method, string endpoint, NPlusOneReport report);
}
