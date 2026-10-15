using System.Buffers;
using System.Text;
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
/// String literals and comments are found in a left-to-right scan, so a quote inside a comment or
/// <c>--</c> inside a string cannot hide or expose the text after it. Dialects disagree on two things, and a literal read
/// the wrong way shows the text after it as if it were code: whether a backslash escapes a quote (MySQL, PostgreSQL
/// <c>E''</c>, and others under some server settings) and whether a double-quoted run is a name (PostgreSQL, SQLite,
/// Oracle) or a value (MySQL, SQL Server with <c>QUOTED_IDENTIFIER</c> off). The scan runs once for every reading the
/// command's <see cref="SqlDialect"/> allows and hides each character that any reading takes for a value or a comment:
/// a known dialect keeps its own code readable (SQL Server's <c>ESCAPE N'\'</c>), an unknown one gets every reading.
/// Block comments nest (PostgreSQL) in every reading. One gap is left on purpose: SQLite takes a double-quoted word that
/// names no column for a string, a legacy behaviour only hand-written SQL relies on, and the fingerprint shows it as the
/// name it looks like, as it must for the names EF Core quotes.
/// </remarks>
internal static partial class SqlFingerprint
{
    /// <summary>Returned for empty command text.</summary>
    private const string Empty = "(empty)";

    /// <summary>A character the scan keeps.</summary>
    private const byte Code = 0;

    /// <summary>A character of a comment: replaced, with the rest of its run, by one space.</summary>
    private const byte Comment = 1;

    /// <summary>A character of a literal: replaced, with the rest of its run, by one <c>?</c>.</summary>
    private const byte Value = 2;

    /// <summary>The only reading of SQL Server, SQLite and Oracle, and of any text without a backslash.</summary>
    private static readonly Backslash[] NeverEscapes = [Backslash.Never];

    /// <summary>PostgreSQL with <c>standard_conforming_strings</c> on (the default), then off.</summary>
    private static readonly Backslash[] PostgreSqlReadings = [Backslash.Prefixed, Backslash.Always];

    /// <summary>MySQL by default, then with <c>NO_BACKSLASH_ESCAPES</c>.</summary>
    private static readonly Backslash[] MySqlReadings = [Backslash.Always, Backslash.Never];

    /// <summary>Every reading, for a dialect the guard does not know.</summary>
    private static readonly Backslash[] AllReadings = [Backslash.Never, Backslash.Prefixed, Backslash.Always];

    /// <summary>Where a backslash inside a quoted run escapes the next character.</summary>
    private enum Backslash
    {
        /// <summary>Nowhere: it is an ordinary character.</summary>
        Never,

        /// <summary>Only inside PostgreSQL's <c>E''</c> literals.</summary>
        Prefixed,

        /// <summary>Inside every quoted run.</summary>
        Always,
    }

    /// <summary>Normalizes <paramref name="sql"/>.</summary>
    /// <param name="sql">The command text.</param>
    /// <param name="dialect">The dialect the command was written in; <see cref="SqlDialect.Unknown"/> hides the most.</param>
    /// <returns>The fingerprint; never contains a literal from the input.</returns>
    public static string Create(string? sql, SqlDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return Empty;
        }

        var text = RemoveStringsAndComments(sql, dialect);
        text = ValueRegex().Replace(text, static match => match.Groups["param"].Success ? "@p" : "?");
        text = InListRegex().Replace(text, "IN (?)");
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length == 0 ? Empty : text;
    }

    /// <summary>
    /// The dialect of an EF Core provider, by its name (<c>context.Database.ProviderName</c>). Only the providers whose
    /// quoting rules are known map to a dialect; any other is <see cref="SqlDialect.Unknown"/> and gets every reading.
    /// </summary>
    /// <param name="providerName">The provider's name, or <see langword="null"/> when there is no context.</param>
    /// <returns>The dialect.</returns>
    public static SqlDialect DialectOf(string? providerName) => providerName switch
    {
        "Microsoft.EntityFrameworkCore.SqlServer" => SqlDialect.SqlServer,
        "Microsoft.EntityFrameworkCore.Sqlite" => SqlDialect.Sqlite,
        "Npgsql.EntityFrameworkCore.PostgreSQL" => SqlDialect.PostgreSql,
        "Pomelo.EntityFrameworkCore.MySql" or "MySql.EntityFrameworkCore" => SqlDialect.MySql,
        "Oracle.EntityFrameworkCore" => SqlDialect.Oracle,
        _ => SqlDialect.Unknown,
    };

    /// <summary>
    /// Replaces every string literal with <c>?</c> and every comment with a space: single-quoted literals (with an
    /// <c>N</c>, <c>E</c>, <c>X</c>, <c>B</c> or <c>U&amp;</c> prefix and doubled quotes inside), Oracle's
    /// <c>q'[...]'</c>, PostgreSQL's dollar quotes (<c>$$...$$</c>, <c>$tag$...$tag$</c>), double-quoted runs that are
    /// values in the dialect, line comments and nested block comments, under every reading of backslashes the dialect
    /// allows. An unterminated literal or comment runs to the end of the text.
    /// </summary>
    /// <param name="sql">The command text.</param>
    /// <param name="dialect">The dialect of the command.</param>
    private static string RemoveStringsAndComments(string sql, SqlDialect dialect)
    {
        var marks = ArrayPool<byte>.Shared.Rent(sql.Length);
        try
        {
            Array.Clear(marks, 0, sql.Length);
            var doubleQuotedNames = dialect is SqlDialect.PostgreSql or SqlDialect.Sqlite or SqlDialect.Oracle;

            // The readings differ only in what a backslash does: without one, they all find the same runs.
            var readings = sql.Contains('\\', StringComparison.Ordinal) ? Readings(dialect) : NeverEscapes;
            foreach (var reading in readings)
            {
                Mark(sql, marks, reading, doubleQuotedNames);
            }

            return Render(sql, marks);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(marks);
        }
    }

    /// <summary>The readings of backslashes a dialect allows, under any server setting.</summary>
    /// <param name="dialect">The dialect.</param>
    private static Backslash[] Readings(SqlDialect dialect) => dialect switch
    {
        SqlDialect.SqlServer or SqlDialect.Sqlite or SqlDialect.Oracle => NeverEscapes,
        SqlDialect.PostgreSql => PostgreSqlReadings,
        SqlDialect.MySql => MySqlReadings,
        _ => AllReadings,
    };

    /// <summary>
    /// Scans the text under one reading and raises the mark of every character this reading finds in a comment or a
    /// literal. Marks only go up, so a character stays hidden once any reading hides it.
    /// </summary>
    /// <param name="sql">The command text.</param>
    /// <param name="marks">One mark per character of <paramref name="sql"/>.</param>
    /// <param name="reading">What a backslash does in this reading.</param>
    /// <param name="doubleQuotedNames">Whether a double-quoted plain identifier is a name, kept in the fingerprint.</param>
    private static void Mark(string sql, byte[] marks, Backslash reading, bool doubleQuotedNames)
    {
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            int end;
            var mark = Value;
            if (c == '-' && At(sql, i + 1, '-'))
            {
                end = sql.AsSpan(i).IndexOfAny('\r', '\n') is var newline and >= 0 ? i + newline : sql.Length;
                mark = Comment;
            }
            else if (c == '/' && At(sql, i + 1, '*'))
            {
                end = SkipBlockComment(sql, i);
                mark = Comment;
            }
            else if (c == '\'' || IsLiteralPrefix(sql, i))
            {
                end = SkipQuoted(sql, i, reading);
            }
            else if (c == '$' && DollarTag(sql, i) is { } tag)
            {
                var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                end = close < 0 ? sql.Length : close + tag.Length;
            }
            else if (c == '"')
            {
                var close = sql.IndexOf('"', i + 1);
                if (doubleQuotedNames && close >= 0 && IsPlainIdentifier(sql.AsSpan(i + 1, close - i - 1)))
                {
                    end = close + 1;
                    mark = Code;
                }
                else
                {
                    end = SkipQuotedRun(sql, i, '"', reading == Backslash.Always);
                }
            }
            else
            {
                i++;
                continue;
            }

            for (var k = i; k < end; k++)
            {
                if (marks[k] < mark)
                {
                    marks[k] = mark;
                }
            }

            i = end;
        }
    }

    /// <summary>Writes the kept characters, one <c>?</c> per run of literal characters and one space per run of comment ones.</summary>
    /// <param name="sql">The command text.</param>
    /// <param name="marks">The marks every reading left.</param>
    private static string Render(string sql, byte[] marks)
    {
        var output = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var mark = marks[i];
            if (mark == Code)
            {
                output.Append(sql[i]);
                i++;
                continue;
            }

            while (i < sql.Length && marks[i] == mark)
            {
                i++;
            }

            output.Append(mark == Value ? '?' : ' ');
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
    /// <c>q'[...]'</c> ends at its closing delimiter and quote, any other at a quote that is neither doubled nor, where
    /// the reading says so, escaped with a backslash.
    /// </summary>
    /// <param name="sql">The text.</param>
    /// <param name="start">Where the literal or its prefix starts.</param>
    /// <param name="reading">What a backslash does in this reading.</param>
    private static int SkipQuoted(string sql, int start, Backslash reading)
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

        var escapes = reading == Backslash.Always || (reading == Backslash.Prefixed && char.ToUpperInvariant(sql[start]) == 'E');
        return SkipQuotedRun(sql, quote, '\'', escapes);
    }

    /// <summary>The end of a run quoted with <paramref name="quote"/>: the first quote that is neither doubled nor escaped.</summary>
    /// <param name="sql">The text.</param>
    /// <param name="open">The position of the opening quote.</param>
    /// <param name="quote">The quote character.</param>
    /// <param name="backslashEscapes">Whether a backslash escapes the next character.</param>
    private static int SkipQuotedRun(string sql, int open, char quote, bool backslashEscapes)
    {
        for (var i = open + 1; i < sql.Length; i++)
        {
            if (backslashEscapes && sql[i] == '\\')
            {
                i++;
            }
            else if (sql[i] == quote)
            {
                if (!At(sql, i + 1, quote))
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
