using System.Text.RegularExpressions;

namespace NPlusOne.EntityFrameworkCore.Recording;

/// <summary>
/// An immutable copy of <see cref="NPlusOneOptions"/> taken when a scope begins, so a shared options
/// instance changed later (or concurrently) cannot change a running scope.
/// </summary>
/// <param name="RepeatThreshold">Runs a group may reach before it is a suspect.</param>
/// <param name="IncludeWrites">Whether write commands are recorded.</param>
/// <param name="CaptureCallSite">Whether groups include the call site.</param>
/// <param name="MaxSqlPreviewLength">Maximum length of the SQL shown in reports.</param>
/// <param name="IgnoredSqlPatterns">Commands matching any of these are not recorded.</param>
internal sealed record ScopeSettings(
    int RepeatThreshold,
    bool IncludeWrites,
    bool CaptureCallSite,
    int MaxSqlPreviewLength,
    Regex[] IgnoredSqlPatterns)
{
    /// <summary>Copies and validates the options.</summary>
    /// <param name="options">The options to copy.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentException">An ignored pattern is <see langword="null"/>.</exception>
    public static ScopeSettings From(NPlusOneOptions options)
    {
        Regex[] patterns = [.. options.IgnoredSqlPatterns];
        if (Array.IndexOf(patterns, null) >= 0)
        {
            throw new ArgumentException("IgnoredSqlPatterns must not contain null.", nameof(options));
        }

        return new ScopeSettings(options.RepeatThreshold, options.IncludeWrites, options.CaptureCallSite, options.MaxSqlPreviewLength, patterns);
    }

    /// <summary>Whether a command of this kind and text is recorded by the scope.</summary>
    /// <param name="kind">Query or write.</param>
    /// <param name="sql">The command text.</param>
    /// <returns><see langword="false"/> for writes when they are excluded and for ignored SQL.</returns>
    public bool Accepts(NPlusOneSuspectKind kind, string sql)
    {
        if (kind == NPlusOneSuspectKind.Write && !IncludeWrites)
        {
            return false;
        }

        foreach (var pattern in IgnoredSqlPatterns)
        {
            if (pattern.IsMatch(sql))
            {
                return false;
            }
        }

        return true;
    }
}
