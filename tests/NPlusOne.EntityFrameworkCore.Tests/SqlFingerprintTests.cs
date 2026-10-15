using System.Text.RegularExpressions;
using NPlusOne.EntityFrameworkCore.Recording;

namespace NPlusOne.EntityFrameworkCore.Tests;

public sealed class SqlFingerprintTests
{
    [Theory]
    [InlineData("SELECT * FROM t WHERE id = 42", "SELECT * FROM t WHERE id = ?")]
    [InlineData("SELECT * FROM t WHERE price > 3.14 AND e < 1e-5 AND f = .5", "SELECT * FROM t WHERE price > ? AND e < ? AND f = ?")]
    [InlineData("SELECT * FROM t WHERE name = 'O''Brien' AND n = N'x'", "SELECT * FROM t WHERE name = ? AND n = ?")]
    [InlineData("SELECT * FROM t WHERE id = '550e8400-e29b-41d4-a716-446655440000'", "SELECT * FROM t WHERE id = ?")]
    [InlineData("SELECT * FROM t WHERE id = 550e8400-e29b-41d4-a716-446655440000", "SELECT * FROM t WHERE id = ?")]
    [InlineData("SELECT * FROM t WHERE b = 0x1F2E", "SELECT * FROM t WHERE b = ?")]
    [InlineData("SELECT * FROM t WHERE a = @__customer_Id_0 AND b = @p1 AND c = :name AND d = $1", "SELECT * FROM t WHERE a = @p AND b = @p AND c = @p AND d = @p")]
    [InlineData("SELECT @@ROWCOUNT, x::int FROM t", "SELECT @@ROWCOUNT, x::int FROM t")]
    [InlineData("SELECT  *\r\n  FROM\tt", "SELECT * FROM t")]
    [InlineData("-- tag with 'quote\nSELECT 1 /* id = 5 */ FROM t", "SELECT ? FROM t")]
    [InlineData("SELECT * FROM t WHERE s = 'a -- not a comment' AND x = 1", "SELECT * FROM t WHERE s = ? AND x = ?")]
    [InlineData("SELECT * FROM t WHERE s = 'unterminated secret", "SELECT * FROM t WHERE s = ?")]
    [InlineData("", "(empty)")]
    public void Normalizes_values(string sql, string expected)
    {
        foreach (var dialect in Enum.GetValues<SqlDialect>())
        {
            Assert.Equal(expected, SqlFingerprint.Create(sql, dialect));
        }
    }

    // The value, IN list and whitespace steps once ran as three regex replacements; they now write into pooled buffers.
    // Text without strings or comments, where the first step changes nothing, must come out as the replacements made it.
    [Fact]
    public void Values_in_lists_and_whitespace_come_out_as_the_regex_replacements_made_them()
    {
        string[] tokens =
        [
            "SELECT", "IN", "in", "In", "(", ")", ",", "@p0", "@__id_1", ":name", "$1", "@@ROWCOUNT", "x::int", "42", "3.14",
            "1e-5", "2E+10", ".5", "0x1F", "0X0", "550e8400-e29b-41d4-a716-446655440000", "NULL", "null", "-1", "abc",
            "a_1", "t.col", "Id2", "e5", "?", "@p", "-?",
        ];
        string[] separators = ["", " ", "  ", "\t", "\n", "\r\n", "\u00A0", ", "];
        var random = new Random(20261015);
        for (var run = 0; run < 5000; run++)
        {
            var text = new System.Text.StringBuilder();
            var count = random.Next(1, 16);
            for (var i = 0; i < count; i++)
            {
                text.Append(separators[random.Next(separators.Length)]).Append(tokens[random.Next(tokens.Length)]);
            }

            var sql = text.Append(separators[random.Next(separators.Length)]).ToString();
            var expected = ByRegexReplacements(sql);
            foreach (var dialect in Enum.GetValues<SqlDialect>())
            {
                Assert.True(expected == SqlFingerprint.Create(sql, dialect), $"{dialect}: '{sql}'");
            }
        }
    }

    private static string ByRegexReplacements(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return "(empty)";
        }

        var text = Regex.Replace(
            sql,
            """(?<![\w@:$])(?:(?<param>[@:$]\w+)|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|\.\d+)(?!\w)""",
            static match => match.Groups["param"].Success ? "@p" : "?",
            RegexOptions.CultureInvariant);
        text = Regex.Replace(text, """\bIN\s*\(\s*(?:@p|-?\?|NULL)(?:\s*,\s*(?:@p|-?\?|NULL))*\s*\)""", "IN (?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, """\s+""", " ", RegexOptions.CultureInvariant).Trim();
        return text.Length == 0 ? "(empty)" : text;
    }

    [Fact]
    public void A_command_text_sent_again_is_fingerprinted_once()
    {
        var sql = new string("SELECT * FROM t WHERE id = @p0 AND name = 'secret'");

        var first = SqlFingerprint.Of(sql, SqlDialect.SqlServer);

        Assert.Equal("SELECT * FROM t WHERE id = @p AND name = ?", first);
        Assert.Same(first, SqlFingerprint.Of(sql, SqlDialect.SqlServer));
    }

    [Fact]
    public void A_scope_records_the_fingerprint_kept_for_the_text()
    {
        var sql = new string("SELECT * FROM t WHERE id = @p0");
        using var scope = NPlusOneGuard.Begin(o => o.CaptureCallSite = false);
        for (var i = 0; i < 3; i++)
        {
            NPlusOneScope.RecordCommand(scope, NPlusOneSuspectKind.Query, sql, SqlDialect.Sqlite);
        }

        var suspect = Assert.Single(scope.Report.Suspects);
        Assert.Same(SqlFingerprint.Of(sql, SqlDialect.Sqlite), suspect.Sql);
    }

    [Fact]
    public void A_command_text_seen_in_another_dialect_is_read_in_that_dialect()
    {
        var sql = new string("SELECT * FROM t WHERE name = \"secret\"");

        Assert.Equal("SELECT * FROM t WHERE name = \"secret\"", SqlFingerprint.Of(sql, SqlDialect.PostgreSql));
        Assert.Equal("SELECT * FROM t WHERE name = ?", SqlFingerprint.Of(sql, SqlDialect.MySql));
    }

    // Text every dialect reads the same way: no literal shows in any of them.
    [Theory]
    [InlineData("SELECT $$top secret$$, $tag$top secret$tag$ FROM t WHERE a = $1", "SELECT ?, ? FROM t WHERE a = @p")]
    [InlineData("SELECT * FROM t WHERE email = \"alice@secret.com\"", "SELECT * FROM t WHERE email = ?")]
    [InlineData("SELECT 1 /* a /* b */ leaked secret */ FROM t", "SELECT ? FROM t")]
    [InlineData("SELECT * FROM t WHERE name = q'[O'Brien secret]'", "SELECT * FROM t WHERE name = ?")]
    [InlineData("SELECT * FROM t WHERE name = U&'secret' AND b = B'0101'", "SELECT * FROM t WHERE name = ? AND b = ?")]
    [InlineData("SELECT * FROM t WHERE id IN (-1, 2, -3)", "SELECT * FROM t WHERE id IN (?)")]
    public void Never_shows_a_literal_in_any_dialect(string sql, string expected)
    {
        foreach (var dialect in Enum.GetValues<SqlDialect>())
        {
            var fingerprint = SqlFingerprint.Create(sql, dialect);
            Assert.Equal(expected, fingerprint);
            Assert.DoesNotContain("secret", fingerprint, StringComparison.Ordinal);
        }
    }

    // Dialects disagree on backslashes and double quotes. A known dialect is read its own way, under every server setting
    // that changes the reading (MySQL's NO_BACKSLASH_ESCAPES, PostgreSQL's standard_conforming_strings); an unknown one
    // under every reading. A character any of these readings takes for a value is hidden.
    [Theory]
    [InlineData("SqlServer", "SELECT * FROM t WHERE path = N'C:\\' AND name = N'secret'", "SELECT * FROM t WHERE path = ? AND name = ?")]
    [InlineData("Sqlite", "SELECT * FROM t WHERE path = 'C:\\' AND name = 'secret'", "SELECT * FROM t WHERE path = ? AND name = ?")]
    [InlineData("Oracle", "SELECT * FROM t WHERE path = 'C:\\' AND name = 'secret'", "SELECT * FROM t WHERE path = ? AND name = ?")]
    [InlineData("PostgreSql", "SELECT * FROM t WHERE path = 'C:\\' AND name = 'secret'", "SELECT * FROM t WHERE path = ?")]
    [InlineData("MySql", "SELECT * FROM t WHERE path = 'C:\\' AND name = 'secret'", "SELECT * FROM t WHERE path = ?")]
    [InlineData("Unknown", "SELECT * FROM t WHERE path = 'C:\\' AND name = 'secret'", "SELECT * FROM t WHERE path = ?")]
    [InlineData("MySql", "SELECT * FROM t WHERE pw = 'abc\\' OR secret word'", "SELECT * FROM t WHERE pw = ?")]
    [InlineData("PostgreSql", "SELECT * FROM t WHERE pw = 'abc\\' OR secret word'", "SELECT * FROM t WHERE pw = ?")]
    [InlineData("PostgreSql", "SELECT * FROM t WHERE pw = E'abc\\' OR secret word'", "SELECT * FROM t WHERE pw = ?")]
    [InlineData("Unknown", "SELECT * FROM t WHERE pw = 'abc\\' OR secret word'", "SELECT * FROM t WHERE pw = ?")]
    [InlineData("PostgreSql", "SELECT E'a\\'b', 'C:\\', 'secret' FROM t", "SELECT ?, ?")]
    [InlineData("MySql", "SELECT * FROM t WHERE name = \"secret\"", "SELECT * FROM t WHERE name = ?")]
    [InlineData("MySql", "SELECT * FROM t WHERE name = \"abc\\\" OR secret\"", "SELECT * FROM t WHERE name = ?")]
    [InlineData("SqlServer", "SELECT * FROM t WHERE name = \"secret\"", "SELECT * FROM t WHERE name = ?")]
    [InlineData("Unknown", "SELECT * FROM t WHERE name = \"secret\"", "SELECT * FROM t WHERE name = ?")]
    public void Never_shows_a_literal_in_its_dialect(string dialect, string sql, string expected)
    {
        var fingerprint = SqlFingerprint.Create(sql, Enum.Parse<SqlDialect>(dialect));
        Assert.Equal(expected, fingerprint);
        Assert.DoesNotContain("secret", fingerprint, StringComparison.Ordinal);
    }

    // The other side: what a dialect reads as code or as a name stays readable.
    [Theory]
    [InlineData("SqlServer", "SELECT [c].[Id] FROM [Customers] AS [c] WHERE [c].[Name] LIKE @__p_0 ESCAPE N'\\' AND [c].[Id] > 3", "SELECT [c].[Id] FROM [Customers] AS [c] WHERE [c].[Name] LIKE @p ESCAPE ? AND [c].[Id] > ?")]
    [InlineData("Sqlite", "SELECT \"c\".\"Id\" FROM \"Customers\" AS \"c\" WHERE \"c\".\"Name\" LIKE @__p_0 ESCAPE '\\' AND \"c\".\"Id\" > 3", "SELECT \"c\".\"Id\" FROM \"Customers\" AS \"c\" WHERE \"c\".\"Name\" LIKE @p ESCAPE ? AND \"c\".\"Id\" > ?")]
    [InlineData("PostgreSql", "SELECT \"t0\".\"Col1\" FROM \"Order Details\" AS \"t0\"", "SELECT \"t0\".\"Col1\" FROM \"Order Details\" AS \"t0\"")]
    [InlineData("Sqlite", "SELECT \"t0\".\"Col1\" FROM \"Order Details\" AS \"t0\"", "SELECT \"t0\".\"Col1\" FROM \"Order Details\" AS \"t0\"")]
    [InlineData("Oracle", "SELECT \"t0\".\"Col1\" FROM \"Order Details\" AS \"t0\"", "SELECT \"t0\".\"Col1\" FROM \"Order Details\" AS \"t0\"")]
    [InlineData("MySql", "SELECT `t0`.`Col1` FROM `Order Details` AS `t0`", "SELECT `t0`.`Col1` FROM `Order Details` AS `t0`")]
    public void Keeps_code_and_names_readable(string dialect, string sql, string expected)
        => Assert.Equal(expected, SqlFingerprint.Create(sql, Enum.Parse<SqlDialect>(dialect)));

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "SqlServer")]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", "Sqlite")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "PostgreSql")]
    [InlineData("Pomelo.EntityFrameworkCore.MySql", "MySql")]
    [InlineData("MySql.EntityFrameworkCore", "MySql")]
    [InlineData("Oracle.EntityFrameworkCore", "Oracle")]
    [InlineData("FirebirdSql.EntityFrameworkCore.Firebird", "Unknown")]
    [InlineData(null, "Unknown")]
    public void Reads_the_dialect_from_the_provider(string? providerName, string expected)
        => Assert.Equal(Enum.Parse<SqlDialect>(expected), SqlFingerprint.DialectOf(providerName));

    [Fact]
    public void In_lists_of_different_lengths_share_one_shape()
    {
        var two = SqlFingerprint.Create("SELECT * FROM t WHERE id IN (@p0, @p1)", SqlDialect.Sqlite);
        var five = SqlFingerprint.Create("SELECT * FROM t WHERE id IN (@p0,@p1,@p2,@p3,@p4)", SqlDialect.Sqlite);
        var literals = SqlFingerprint.Create("SELECT * FROM t WHERE id IN (1, 2, 3, NULL)", SqlDialect.Sqlite);
        var strings = SqlFingerprint.Create("SELECT * FROM t WHERE id in ('a', 'b')", SqlDialect.Sqlite);

        Assert.Equal("SELECT * FROM t WHERE id IN (?)", two);
        Assert.Equal(two, five);
        Assert.Equal(two, literals);
        Assert.Equal(two, strings);
    }

    [Fact]
    public void Different_values_share_one_fingerprint()
    {
        Assert.Equal(
            SqlFingerprint.Create("SELECT * FROM t WHERE a = 1 AND b = 'x'", SqlDialect.Sqlite),
            SqlFingerprint.Create("SELECT * FROM t WHERE a = 99 AND b = 'something else'", SqlDialect.Sqlite));
    }
}
