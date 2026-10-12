using System.Text.RegularExpressions;

namespace NPlusOne.EntityFrameworkCore.Recording;

/// <summary>
/// Turns SQL text into its shape: string literals, numbers, GUIDs and hexadecimal literals become
/// <c>?</c>, parameter names become <c>@p</c>, comments (including EF Core query tags) are removed,
/// <c>IN</c> lists collapse to one placeholder and whitespace collapses to single spaces.
/// </summary>
/// <remarks>
/// Two jobs depend on it. Grouping: the same query with different values, differently named
/// parameters or a list of a different length has one fingerprint. Privacy: the fingerprint is the
/// only SQL a report shows, so no value from the command can reach a log or an exception message.
/// String literals and comments are removed in one left-to-right scan, so a quote inside a comment or
/// <c>--</c> inside a string cannot hide or expose the text after it. Where SQL dialects disagree, the scan
/// takes the reading that hides more: a backslash escapes the next character inside a literal (MySQL,
/// PostgreSQL <c>E''</c>), block comments nest (PostgreSQL), and a double-quoted run that is not a plain
/// identifier is a value (MySQL strings) rather than a name.
/// </remarks>
internal static partial class SqlFingerprint
{
    /// <summary>Returned for empty command text.</summary>
    private const string Empty = "(empty)";

    /// <summary>Normalizes <paramref name="sql"/>.</summary>
    /// <param name="sql">The command text.</param>
    /// <returns>The fingerprint; never contains a literal from the input.</returns>
    public static string Create(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return Empty;
        }

        var text = RemoveStringsAndComments(sql);
        text = ValueRegex().Replace(text, static match => match.Groups["param"].Success ? "@p" : "?");
        text = InListRegex().Replace(text, "IN (?)");
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length == 0 ? Empty : text;
    }

    /// <summary>
    /// Replaces every string literal with <c>?</c> and every comment with a space: single-quoted literals (with an
    /// <c>N</c>, <c>E</c>, <c>X</c>, <c>B</c> or <c>U&amp;</c> prefix, doubled quotes and backslash escapes inside),
    /// Oracle's <c>q'[...]'</c>, PostgreSQL's dollar quotes (<c>$$...$$</c>, <c>$tag$...$tag$</c>), double-quoted runs
    /// that are not plain identifiers, line comments and nested block comments. An unterminated literal or comment
    /// runs to the end of the text.
    /// </summary>
    /// <param name="sql">The command text.</param>
    private static string RemoveStringsAndComments(string sql)
    {
        var output = new System.Text.StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && At(sql, i + 1, '-'))
            {
                i = sql.IndexOfAny(['\r', '\n'], i) is var end and >= 0 ? end : sql.Length;
                output.Append(' ');
            }
            else if (c == '/' && At(sql, i + 1, '*'))
            {
                i = SkipBlockComment(sql, i);
                output.Append(' ');
            }
            else if (c == '\'' || IsLiteralPrefix(sql, i))
            {
                i = SkipQuoted(sql, i);
                output.Append('?');
            }
            else if (c == '$' && DollarTag(sql, i) is { } tag)
            {
                var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + tag.Length;
                output.Append('?');
            }
            else if (c == '"')
            {
                var close = sql.IndexOf('"', i + 1);
                var end = close < 0 ? sql.Length : close + 1;
                var content = sql.AsSpan(i + 1, Math.Max(0, (close < 0 ? sql.Length : close) - i - 1));
                output.Append(IsPlainIdentifier(content) && close >= 0 ? sql.AsSpan(i, end - i) : "?");
                i = end;
            }
            else
            {
                output.Append(c);
                i++;
            }
        }

        return output.ToString();
    }

    /// <summary>Whether the text has the character at the position.</summary>
    /// <param name="sql">The text.</param>
    /// <param name="index">The position.</param>
    /// <param name="c">The character.</param>
    private static bool At(string sql, int index, char c) => index < sql.Length && sql[index] == c;

    /// <summary>
    /// Whether a literal prefix starts here (<c>N'</c>, <c>E'</c>, <c>X'</c>, <c>B'</c>, <c>U&amp;'</c>, Oracle's
    /// <c>q'</c>), not as the end of a longer word.
    /// </summary>
    /// <param name="sql">The text.</param>
    /// <param name="index">The position of the prefix.</param>
    private static bool IsLiteralPrefix(string sql, int index)
    {
        if (index > 0 && (char.IsLetterOrDigit(sql[index - 1]) || sql[index - 1] == '_'))
        {
            return false;
        }

        return char.ToUpperInvariant(sql[index]) switch
        {
            'N' or 'E' or 'X' or 'B' or 'Q' => At(sql, index + 1, '\''),
            'U' => At(sql, index + 1, '&') && At(sql, index + 2, '\''),
            _ => false,
        };
    }

    /// <summary>
    /// The end of a quoted literal that starts at <paramref name="start"/> (with or without a prefix): Oracle's
    /// <c>q'[...]'</c> ends at its closing delimiter and quote, any other at a quote that is neither doubled nor escaped
    /// with a backslash.
    /// </summary>
    /// <param name="sql">The text.</param>
    /// <param name="start">Where the literal or its prefix starts.</param>
    private static int SkipQuoted(string sql, int start)
    {
        var quote = sql.IndexOf('\'', start);
        if (char.ToUpperInvariant(sql[start]) == 'Q' && quote == start + 1 && quote + 1 < sql.Length)
        {
            var open = sql[quote + 1];
            var closing = open switch
            {
                '[' => ']',
                '(' => ')',
                '{' => '}',
                '<' => '>',
                _ => open,
            };
            var end = sql.IndexOf(closing + "'", quote + 2, StringComparison.Ordinal);
            return end < 0 ? sql.Length : end + 2;
        }

        for (var i = quote + 1; i < sql.Length; i++)
        {
            if (sql[i] == '\\')
            {
                i++;
            }
            else if (sql[i] == '\'')
            {
                if (!At(sql, i + 1, '\''))
                {
                    return i + 1;
                }

                i++;
            }
        }

        return sql.Length;
    }

    /// <summary>The end of a block comment starting at <paramref name="start"/>, counting nested comments.</summary>
    /// <param name="sql">The text.</param>
    /// <param name="start">Where the comment starts.</param>
    private static int SkipBlockComment(string sql, int start)
    {
        var depth = 0;
        for (var i = start; i < sql.Length - 1; i++)
        {
            if (sql[i] == '/' && sql[i + 1] == '*')
            {
                depth++;
                i++;
            }
            else if (sql[i] == '*' && sql[i + 1] == '/')
            {
                depth--;
                i++;
                if (depth == 0)
                {
                    return i + 1;
                }
            }
        }

        return sql.Length;
    }

    /// <summary>
    /// The opening tag of a PostgreSQL dollar quote at the position (<c>$$</c> or <c>$tag$</c>), or null: a parameter
    /// such as <c>$1</c> is not one.
    /// </summary>
    /// <param name="sql">The text.</param>
    /// <param name="index">The position of the dollar sign.</param>
    private static string? DollarTag(string sql, int index)
    {
        if (index > 0 && (char.IsLetterOrDigit(sql[index - 1]) || sql[index - 1] == '_'))
        {
            return null;
        }

        var i = index + 1;
        if (i < sql.Length && (char.IsLetter(sql[i]) || sql[i] == '_'))
        {
            while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
            {
                i++;
            }
        }

        return At(sql, i, '$') ? sql.Substring(index, i - index + 1) : null;
    }

    /// <summary>Whether a double-quoted run is a plain identifier (letters, digits, underscores, spaces), not a value.</summary>
    /// <param name="content">The text between the quotes.</param>
    private static bool IsPlainIdentifier(ReadOnlySpan<char> content)
    {
        if (content.IsEmpty)
        {
            return false;
        }

        foreach (var c in content)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != ' ')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A parameter (<c>@name</c>, <c>:name</c>, <c>$1</c>, but not <c>@@ROWCOUNT</c> or a <c>::</c>
    /// cast), a GUID, a hexadecimal literal or a decimal number that is not part of an identifier.
    /// </summary>
    /// <returns>The source-generated regex.</returns>
    [GeneratedRegex("""(?<![\w@:$])(?:(?<param>[@:$]\w+)|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|\.\d+)(?!\w)""", RegexOptions.CultureInvariant)]
    private static partial Regex ValueRegex();

    /// <summary>An <c>IN</c> list made only of placeholders, parameters and NULLs, of any length.</summary>
    /// <returns>The source-generated regex.</returns>
    [GeneratedRegex("""\bIN\s*\(\s*(?:@p|-?\?|NULL)(?:\s*,\s*(?:@p|-?\?|NULL))*\s*\)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InListRegex();

    /// <summary>Any run of whitespace.</summary>
    /// <returns>The source-generated regex.</returns>
    [GeneratedRegex("""\s+""", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
