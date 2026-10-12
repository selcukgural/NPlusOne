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
    [InlineData("SELECT \"t0\".\"Col1\" FROM \"Table2\" AS \"t0\"", "SELECT \"t0\".\"Col1\" FROM \"Table2\" AS \"t0\"")]
    [InlineData("SELECT  *\r\n  FROM\tt", "SELECT * FROM t")]
    [InlineData("-- tag with 'quote\nSELECT 1 /* id = 5 */ FROM t", "SELECT ? FROM t")]
    [InlineData("SELECT * FROM t WHERE s = 'a -- not a comment' AND x = 1", "SELECT * FROM t WHERE s = ? AND x = ?")]
    [InlineData("SELECT * FROM t WHERE s = 'unterminated secret", "SELECT * FROM t WHERE s = ?")]
    [InlineData("", "(empty)")]
    public void Normalizes_values(string sql, string expected) => Assert.Equal(expected, SqlFingerprint.Create(sql));

    // Dialects disagree on escapes, quotes and comments; the fingerprint takes the reading that hides more.
    [Theory]
    [InlineData("SELECT * FROM t WHERE pw = 'abc\\' OR secret word'", "SELECT * FROM t WHERE pw = ?")]
    [InlineData("SELECT * FROM t WHERE pw = E'abc\\' OR secret word'", "SELECT * FROM t WHERE pw = ?")]
    [InlineData("SELECT $$top secret$$, $tag$top secret$tag$ FROM t WHERE a = $1", "SELECT ?, ? FROM t WHERE a = @p")]
    [InlineData("SELECT * FROM t WHERE email = \"alice@example.com\"", "SELECT * FROM t WHERE email = ?")]
    [InlineData("SELECT 1 /* a /* b */ leaked secret */ FROM t", "SELECT ? FROM t")]
    [InlineData("SELECT * FROM t WHERE name = q'[O'Brien secret]'", "SELECT * FROM t WHERE name = ?")]
    [InlineData("SELECT * FROM t WHERE name = U&'secret' AND b = B'0101'", "SELECT * FROM t WHERE name = ? AND b = ?")]
    [InlineData("SELECT \"Order Details\".\"Id\" FROM \"Order Details\"", "SELECT \"Order Details\".\"Id\" FROM \"Order Details\"")]
    [InlineData("SELECT * FROM t WHERE id IN (-1, 2, -3)", "SELECT * FROM t WHERE id IN (?)")]
    public void Never_shows_a_literal_in_any_dialect(string sql, string expected)
    {
        var fingerprint = SqlFingerprint.Create(sql);
        Assert.Equal(expected, fingerprint);
        Assert.DoesNotContain("secret", fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void In_lists_of_different_lengths_share_one_shape()
    {
        var two = SqlFingerprint.Create("SELECT * FROM t WHERE id IN (@p0, @p1)");
        var five = SqlFingerprint.Create("SELECT * FROM t WHERE id IN (@p0,@p1,@p2,@p3,@p4)");
        var literals = SqlFingerprint.Create("SELECT * FROM t WHERE id IN (1, 2, 3, NULL)");
        var strings = SqlFingerprint.Create("SELECT * FROM t WHERE id in ('a', 'b')");

        Assert.Equal("SELECT * FROM t WHERE id IN (?)", two);
        Assert.Equal(two, five);
        Assert.Equal(two, literals);
        Assert.Equal(two, strings);
    }

    [Fact]
    public void Different_values_share_one_fingerprint()
    {
        Assert.Equal(
            SqlFingerprint.Create("SELECT * FROM t WHERE a = 1 AND b = 'x'"),
            SqlFingerprint.Create("SELECT * FROM t WHERE a = 99 AND b = 'something else'"));
    }
}
