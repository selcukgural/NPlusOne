using System.Runtime.CompilerServices;
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
    /// <summary>
    /// How long an ignored pattern without a timeout of its own may run on one command. The patterns run on the SQL text,
    /// which can carry values inlined into it (<c>EF.Constant</c>, raw SQL): a pattern that backtracks must not hold the
    /// thread of the request it observes. A match that times out drops that one record (see the interceptor).
    /// </summary>
    internal static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The bounded copy of each pattern without a timeout, built once per pattern instance rather than once per scope,
    /// since a scope begins on every request and building a <see cref="Regex"/> parses it again.
    /// </summary>
    private static readonly ConditionalWeakTable<Regex, Regex> Bounded = [];

    /// <summary>Copies and validates the options.</summary>
    /// <param name="options">The options to copy.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentException">An ignored pattern is <see langword="null"/>.</exception>
    public static ScopeSettings From(NPlusOneOptions options)
    {
        var patterns = new Regex[options.IgnoredSqlPatterns.Count];
        for (var i = 0; i < patterns.Length; i++)
        {
            patterns[i] = options.IgnoredSqlPatterns[i] is { } pattern
                ? Bound(pattern)
                : throw new ArgumentException("IgnoredSqlPatterns must not contain null.", nameof(options));
        }

        return new ScopeSettings(options.RepeatThreshold, options.IncludeWrites, options.CaptureCallSite, options.MaxSqlPreviewLength, patterns);
    }

    /// <summary>The pattern itself when it has a timeout, otherwise a copy that stops after <see cref="PatternTimeout"/>.</summary>
    /// <param name="pattern">An ignored pattern from the options.</param>
    /// <returns>A pattern with a finite match timeout.</returns>
    private static Regex Bound(Regex pattern) =>
        pattern.MatchTimeout != Regex.InfiniteMatchTimeout
            ? pattern
            : Bounded.GetValue(pattern, static p => new Regex(p.ToString(), p.Options, PatternTimeout));

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
