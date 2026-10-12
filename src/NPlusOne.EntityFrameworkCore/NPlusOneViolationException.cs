namespace NPlusOne.EntityFrameworkCore;

/// <summary>
/// Thrown when a scope that must be free of N+1 patterns has suspects. The message is the readable
/// report; <see cref="Report"/> holds the same data for inspection.
/// </summary>
public sealed class NPlusOneViolationException : Exception
{
    /// <summary>Creates the exception with a generic message and no report.</summary>
    public NPlusOneViolationException()
        : base("The N+1 guard found repeated queries or lazy loads.")
    {
    }

    /// <summary>Creates the exception with a custom message and no report.</summary>
    /// <param name="message">The message that describes the violation.</param>
    public NPlusOneViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message, an inner exception and no report.</summary>
    /// <param name="message">The message that describes the violation.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public NPlusOneViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a report; the message is the report's text.</summary>
    /// <param name="report">The report of the scope that has suspects.</param>
    public NPlusOneViolationException(NPlusOneReport report)
        : base((report ?? throw new ArgumentNullException(nameof(report))).ToString()) =>
        Report = report;

    /// <summary>The report of the failing scope, or <see langword="null"/> when created without one.</summary>
    public NPlusOneReport? Report { get; }
}
