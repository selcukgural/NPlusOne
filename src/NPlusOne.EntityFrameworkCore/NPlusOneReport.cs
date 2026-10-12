using System.Globalization;
using System.Text;

namespace NPlusOne.EntityFrameworkCore;

/// <summary>
/// What a scope recorded: the suspects and the totals. Holds normalized SQL, navigation names and
/// call sites only, so it is safe to log or put in an exception message.
/// </summary>
public sealed class NPlusOneReport
{
    /// <summary>Creates a report; only the guard creates them.</summary>
    /// <param name="suspects">The groups over the threshold, most frequent first.</param>
    /// <param name="commandCount">How many commands the scope recorded.</param>
    /// <param name="lazyLoadCount">How many lazy loads the scope recorded.</param>
    /// <param name="repeatThreshold">The threshold the suspects were measured against.</param>
    internal NPlusOneReport(IReadOnlyList<NPlusOneSuspect> suspects, int commandCount, int lazyLoadCount, int repeatThreshold)
    {
        Suspects = suspects;
        CommandCount = commandCount;
        LazyLoadCount = lazyLoadCount;
        RepeatThreshold = repeatThreshold;
    }

    /// <summary>The groups that ran more than <see cref="RepeatThreshold"/> times, most frequent first.</summary>
    public IReadOnlyList<NPlusOneSuspect> Suspects { get; }

    /// <summary>Whether the scope has at least one suspect.</summary>
    public bool HasSuspects => Suspects.Count > 0;

    /// <summary>How many commands the scope recorded (after ignored patterns and the write filter).</summary>
    public int CommandCount { get; }

    /// <summary>How many lazy loads the scope recorded.</summary>
    public int LazyLoadCount { get; }

    /// <summary>The <see cref="NPlusOneOptions.RepeatThreshold"/> of the scope.</summary>
    public int RepeatThreshold { get; }

    /// <summary>
    /// Formats the report: a summary line, one entry per suspect (count, kind, normalized SQL or
    /// navigation, call site) and a hint on how to fix it.
    /// </summary>
    /// <returns>A readable, multi-line report without parameter values.</returns>
    public override string ToString()
    {
        var culture = CultureInfo.InvariantCulture;
        if (!HasSuspects)
        {
            return string.Create(culture, $"N+1 guard found no suspects ({CommandCount} commands, {LazyLoadCount} lazy loads recorded).");
        }

        var builder = new StringBuilder();
        builder.Append(culture, $"N+1 guard found {Suspects.Count} suspect(s): the same work ran more than {RepeatThreshold} times in one scope ");
        builder.Append(culture, $"({CommandCount} commands, {LazyLoadCount} lazy loads recorded).");
        builder.AppendLine();
        foreach (var suspect in Suspects)
        {
            builder.Append("  - ").AppendLine(suspect.ToString());
        }

        builder.Append("Load the data once for all elements instead: Include, a projection, or one query with Contains followed by a lookup.");
        return builder.ToString();
    }
}
