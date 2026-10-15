using BenchmarkDotNet.Attributes;
using NPlusOne.EntityFrameworkCore.Recording;

namespace NPlusOne.Benchmarks.Runtime;

/// <summary>
/// The fingerprint of one command, as the interceptor computes it for every command a scope records: a short query, a
/// long query with joins as EF Core writes them, an <c>IN</c> list, and a literal with a backslash, which an unknown
/// dialect reads three ways.
/// </summary>
public class FingerprintBenchmarks
{
    /// <summary>The commands, by name.</summary>
    private static readonly Dictionary<string, (string Sql, SqlDialect Dialect)> Commands = new()
    {
        ["Short"] = ("""
            SELECT "i"."Id", "i"."Name"
            FROM "Items" AS "i"
            WHERE "i"."Id" = @__id_0
            LIMIT 1
            """, SqlDialect.Sqlite),
        ["Joins"] = ("""
            SELECT [o].[Id], [o].[CreatedAt], [o].[CustomerId], [o].[Status], [o].[Total], [c].[Id], [c].[Email], [c].[Name],
                [l].[Id], [l].[OrderId], [l].[ProductId], [l].[Quantity], [l].[UnitPrice], [p].[Id], [p].[Name], [p].[Sku]
            FROM [Orders] AS [o]
            INNER JOIN [Customers] AS [c] ON [o].[CustomerId] = [c].[Id]
            LEFT JOIN [Lines] AS [l] ON [o].[Id] = [l].[OrderId]
            LEFT JOIN [Products] AS [p] ON [l].[ProductId] = [p].[Id]
            WHERE [o].[CustomerId] = @__customerId_0 AND [o].[Status] <> N'Cancelled' AND [o].[Total] > 100.0
                AND [o].[CreatedAt] >= '2026-01-01T00:00:00.0000000'
            ORDER BY [o].[CreatedAt] DESC, [o].[Id], [c].[Id], [l].[Id]
            """, SqlDialect.SqlServer),
        ["InList"] = ("""
            SELECT "o"."Id", "o"."CustomerId", "o"."Total"
            FROM "Orders" AS "o"
            WHERE "o"."CustomerId" IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24)
            """, SqlDialect.PostgreSql),
        ["Backslash"] = ("""
            SELECT `p`.`Id`, `p`.`Path` FROM `Paths` AS `p` WHERE `p`.`Path` LIKE 'C:\temp\%' ESCAPE '\' AND `p`.`Id` > @p0
            """, SqlDialect.Unknown),
    };

    /// <summary>The command being fingerprinted.</summary>
    private (string Sql, SqlDialect Dialect) _command;

    /// <summary>Which command to fingerprint.</summary>
    [Params("Short", "Joins", "InList", "Backslash")]
    public string Command { get; set; } = "Short";

    /// <summary>Picks the command.</summary>
    [GlobalSetup]
    public void Setup() => _command = Commands[Command];

    /// <summary>One fingerprint.</summary>
    /// <returns>The fingerprint, so the work is not optimised away.</returns>
    [Benchmark]
    public string Create() => SqlFingerprint.Create(_command.Sql, _command.Dialect);
}
