using BenchmarkDotNet.Attributes;
using NPlusOne.EntityFrameworkCore;
using NPlusOne.EntityFrameworkCore.Recording;

namespace NPlusOne.Benchmarks.Runtime;

/// <summary>
/// What a scope costs on its own, without EF Core: the middleware opens one per request and reads its report at the end,
/// and every command of the request is recorded into it.
/// </summary>
public class ScopeBenchmarks
{
    /// <summary>A command as EF Core sends it for a lookup by key.</summary>
    private const string Sql = """
        SELECT "o"."Id", "o"."CustomerId", "o"."Total"
        FROM "Orders" AS "o"
        WHERE "o"."CustomerId" = @__customerId_0
        """;

    /// <summary>The options the middleware would pass, without call sites so only the scope is measured.</summary>
    private readonly NPlusOneOptions _options = new() { CaptureCallSite = false };

    /// <summary>A request that sends no command: begin, read the report, dispose.</summary>
    /// <returns>Whether the report has suspects.</returns>
    [Benchmark(Baseline = true)]
    public bool Empty()
    {
        using var scope = NPlusOneGuard.Begin(_options);
        return scope.Report.HasSuspects;
    }

    /// <summary>A request that sends the same command ten times: an N+1 the report names.</summary>
    /// <returns>Whether the report has suspects.</returns>
    [Benchmark]
    public bool TenCommands()
    {
        using var scope = NPlusOneGuard.Begin(_options);
        for (var i = 0; i < 10; i++)
        {
            NPlusOneScope.RecordCommand(scope, NPlusOneSuspectKind.Query, Sql, SqlDialect.Sqlite);
        }

        return scope.Report.HasSuspects;
    }
}
