namespace NPlusOne.EntityFrameworkCore.Recording;

/// <summary>
/// The database a command was written for, as far as <see cref="SqlFingerprint"/> needs it: dialects disagree on whether a
/// backslash escapes a quote inside a literal and whether double quotes name a column or hold a value, and reading a
/// literal the wrong way shows the text after it.
/// </summary>
internal enum SqlDialect
{
    /// <summary>A provider the guard does not know: every reading is applied, so the fingerprint hides more.</summary>
    Unknown,

    /// <summary>SQL Server: a backslash is an ordinary character; double quotes hold a value when <c>QUOTED_IDENTIFIER</c> is off.</summary>
    SqlServer,

    /// <summary>SQLite: a backslash is an ordinary character; double quotes name a column or table.</summary>
    Sqlite,

    /// <summary>
    /// PostgreSQL: a backslash escapes inside <c>E''</c> literals, and inside every literal when
    /// <c>standard_conforming_strings</c> is off; double quotes name a column or table.
    /// </summary>
    PostgreSql,

    /// <summary>
    /// MySQL and MariaDB: a backslash escapes unless <c>NO_BACKSLASH_ESCAPES</c> is set; double quotes hold a value unless
    /// <c>ANSI_QUOTES</c> is set (EF Core providers quote names with backticks).
    /// </summary>
    MySql,

    /// <summary>Oracle: a backslash is an ordinary character; double quotes name a column or table.</summary>
    Oracle,
}
