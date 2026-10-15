using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// Turns what a method does into text that the summary generator writes into its assembly, and
/// back into a <see cref="MethodSummary"/> for the projects that reference it. Transitive facts are
/// resolved before writing (a public method that calls a private helper that queries is written as
/// querying), so a reader needs no call edges. One line per fact, fields separated by <c>|</c>:
/// <list type="bullet">
/// <item><c>Q|E|op|shape|keyParam|keyMember|paged</c>: runs a query on every call; <c>Q|P2|...</c>: when parameter 2 is an EF query.</item>
/// <item><c>S|...</c>: the same for <c>SaveChanges</c>.</item>
/// <item><c>R|kind|parameter|isQuery|untracked|ignoresAutoIncludes|isElement|path|include,include</c>: what it returns.</item>
/// <item><c>N|parameter|path|perElement</c>: a lazy navigation read on a parameter (<c>-1</c> is <c>this</c>).</item>
/// <item><c>D|ordinal</c>: calls that delegate parameter once per element.</item>
/// </list>
/// </summary>
internal static class SummaryExport
{
    /// <summary>The facts about a method, or null when there are none worth writing.</summary>
    /// <param name="session">The analysis state of the method's compilation.</param>
    /// <param name="method">A method declared in that compilation.</param>
    public static string? Encode(Session session, IMethodSymbol method)
    {
        var lines = new List<string>();
        var unknown = ImmutableArray.CreateRange(method.Parameters, static _ => SourceInfo.Unknown);
        foreach (var (kind, tag) in new[] { (EffectKind.Query, "Q"), (EffectKind.Save, "S") })
        {
            if (session.FindWitness(method, unknown, kind) is { } always)
            {
                lines.Add(Effect(tag, "E", always));
                continue;
            }

            for (var i = 0; i < method.Parameters.Length; i++)
            {
                if (method.Parameters[i].Type.SpecialType != SpecialType.None || method.Parameters[i].Type.IsValueType)
                {
                    continue;
                }

                if (session.FindWitness(method, unknown.SetItem(i, SourceInfo.EfQuery), kind) is { } conditional)
                {
                    lines.Add(Effect(tag, "P" + i.ToString(CultureInfo.InvariantCulture), conditional));
                }
            }
        }

        var returns = session.ReturnsOf(method);
        if (returns.Kind is OriginKind.Ef or OriginKind.NotEf or OriginKind.Parameter)
        {
            lines.Add(Join("R", returns.Kind.ToString(), Number(returns.Parameter), Flag(returns.IsQuery), Flag(returns.Untracked),
                Flag(returns.IgnoresAutoIncludes), Flag(returns.IsElement), returns.Path, string.Join(",", returns.Includes.OrderBy(i => i, StringComparer.Ordinal))));
        }

        for (var i = method.IsStatic ? 0 : ParameterNavigationRead.This; i < method.Parameters.Length; i++)
        {
            foreach (var read in session.NavigationReadsOf(method, i))
            {
                lines.Add(Join("N", Number(read.Parameter), read.Path, Flag(read.PerElement)));
            }
        }

        foreach (var ordinal in session.Summary(method.OriginalDefinition).DelegatesInvokedPerElement.OrderBy(o => o))
        {
            lines.Add(Join("D", Number(ordinal)));
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>
    /// Reads facts written by <see cref="Encode"/> into a summary. The text comes from a referenced assembly, so any
    /// package can put anything there: a line that <see cref="Encode"/> could not have written (an effect source that is
    /// neither <c>E</c> nor <c>P</c> and a number, a shape that is not a <see cref="ShapeKind"/>) is skipped, so a broken
    /// or forged summary makes the analysis see less instead of crashing it. Ordinals need no check here: every reader
    /// looks them up against the call's arguments, and one the method does not have matches nothing.
    /// </summary>
    /// <param name="facts">The text.</param>
    public static MethodSummary Decode(string facts)
    {
        var effects = ImmutableArray.CreateBuilder<Effect>();
        var reads = ImmutableArray.CreateBuilder<ParameterNavigationRead>();
        var delegates = ImmutableHashSet.CreateBuilder<int>();
        var returns = SourceInfo.Unknown;
        foreach (var line in facts.Split('\n'))
        {
            var f = line.Split('|');
            switch (f[0])
            {
                case "Q" or "S" when f.Length >= 7 && Source(f[1]) is { } source && TryParseKind<ShapeKind>(f[3], out var kind):
                    var shape = new QueryShape(kind, f[2], Empty(f[4]), Empty(f[5]), f[6] == "1");
                    effects.Add(new Effect(f[0] == "Q" ? EffectKind.Query : EffectKind.Save, source, f[2], Location.None, shape));
                    break;
                case "R" when f.Length >= 9 && Enum.TryParse<OriginKind>(f[1], out var origin):
                    var includes = f[8].Length == 0 ? ImmutableHashSet<string>.Empty : ImmutableHashSet.CreateRange(f[8].Split(','));
                    returns = new SourceInfo(origin, ParseNumber(f[2]), f[3] == "1", includes, f[7], null, f[4] == "1", f[5] == "1", f[6] == "1");
                    break;
                case "N" when f.Length >= 4:
                    reads.Add(new ParameterNavigationRead(ParseNumber(f[1]), f[2], f[3] == "1"));
                    break;
                case "D" when f.Length >= 2:
                    delegates.Add(ParseNumber(f[1]));
                    break;
            }
        }

        return new MethodSummary(effects.ToImmutable(), ImmutableArray<CallEdge>.Empty, delegates.ToImmutable(), reads.ToImmutable(), returns);
    }

    /// <summary>The source of an effect line: <c>E</c> (on every call), or <c>P</c> and a parameter ordinal.</summary>
    /// <param name="text">The field.</param>
    /// <returns>The source, or null when the field is neither.</returns>
    private static SourceInfo? Source(string text)
    {
        if (text == "E")
        {
            return SourceInfo.EfQuery;
        }

        return text.Length > 1 && text[0] == 'P' ? SourceInfo.FromParameter(ParseNumber(text.Substring(1))) : null;
    }

    /// <summary>Parses an enum member written by name, refusing a number that names no member.</summary>
    /// <param name="text">The field.</param>
    /// <param name="value">The member.</param>
    /// <typeparam name="T">The enum.</typeparam>
    private static bool TryParseKind<T>(string text, out T value)
        where T : struct
        => Enum.TryParse(text, out value) && Enum.IsDefined(typeof(T), value);

    /// <summary>An effect line.</summary>
    /// <param name="tag">Q or S.</param>
    /// <param name="source">E or P and the ordinal.</param>
    /// <param name="witness">The witness found for it.</param>
    private static string Effect(string tag, string source, Witness witness)
        => Join(tag, source, witness.Operator, witness.Shape.Kind.ToString(), witness.Shape.KeyParameter ?? "", witness.Shape.KeyMember ?? "", Flag(witness.Shape.IsPaged));

    /// <summary>Joins fields, keeping the separators out of the values.</summary>
    /// <param name="fields">The fields.</param>
    private static string Join(params string[] fields)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('|');
            }

            builder.Append(fields[i].Replace('|', '_').Replace('\n', ' ').Replace('"', '\''));
        }

        return builder.ToString();
    }

    /// <summary>A boolean as 1 or 0.</summary>
    /// <param name="value">The value.</param>
    private static string Flag(bool value) => value ? "1" : "0";

    /// <summary>An integer, culture-independent.</summary>
    /// <param name="value">The value.</param>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses an integer written by <see cref="Number"/>, or -1.</summary>
    /// <param name="text">The text.</param>
    private static int ParseNumber(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;

    /// <summary>Null for an empty field.</summary>
    /// <param name="text">The field.</param>
    private static string? Empty(string text) => text.Length == 0 ? null : text;
}
