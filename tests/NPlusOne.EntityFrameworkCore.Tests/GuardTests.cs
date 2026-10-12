using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace NPlusOne.EntityFrameworkCore.Tests;

public sealed class GuardTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Query_in_a_loop_is_reported_with_its_call_site()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin();

        var customers = await db.Customers.ToListAsync();
        foreach (var customer in customers)
        {
            _ = await db.Orders.Where(o => o.CustomerId == customer.Id).ToListAsync();
        }

        var suspect = Assert.Single(guard.Report.Suspects);
        Assert.Equal(NPlusOneSuspectKind.Query, suspect.Kind);
        Assert.Equal(TestDatabase.CustomerCount, suspect.Count);
        Assert.Contains("FROM \"Orders\"", suspect.Sql, StringComparison.Ordinal);
        Assert.StartsWith("GuardTests.Query_in_a_loop_is_reported_with_its_call_site (GuardTests.cs:", suspect.CallSite, StringComparison.Ordinal);
        Assert.Equal(TestDatabase.CustomerCount + 1, guard.Report.CommandCount);
    }

    [Fact]
    public void Synchronous_query_in_a_loop_is_reported()
    {
        using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin();

        foreach (var customer in db.Customers.ToList())
        {
            _ = db.Orders.Count(o => o.CustomerId == customer.Id);
        }

        var suspect = Assert.Single(guard.Report.Suspects);
        Assert.Equal(TestDatabase.CustomerCount, suspect.Count);
        Assert.StartsWith("GuardTests.Synchronous_query_in_a_loop_is_reported (", suspect.CallSite, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Include_produces_no_suspect()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin();

        var customers = await db.Customers.Include(c => c.Orders).ToListAsync();

        Assert.Equal(TestDatabase.CustomerCount, customers.Count);
        Assert.False(guard.Report.HasSuspects);
        guard.AssertNoNPlusOne();
    }

    [Fact]
    public async Task Same_sql_from_two_call_sites_under_the_threshold_each_is_not_a_suspect()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin();

        for (var i = 1; i <= 2; i++)
        {
            await LoadOrdersAsync(db, i);
            await LoadOrdersAgainAsync(db, i);
        }

        Assert.Equal(4, guard.Report.CommandCount);
        Assert.False(guard.Report.HasSuspects);
    }

    [Fact]
    public async Task Without_call_sites_the_same_sql_from_two_places_is_one_group()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin(o => o.CaptureCallSite = false);

        for (var i = 1; i <= 2; i++)
        {
            await LoadOrdersAsync(db, i);
            await LoadOrdersAgainAsync(db, i);
        }

        var suspect = Assert.Single(guard.Report.Suspects);
        Assert.Equal(4, suspect.Count);
        Assert.Null(suspect.CallSite);
    }

    [Fact]
    public void Lazy_loading_in_a_loop_is_reported_with_the_navigation()
    {
        using var db = _database.CreateContext(lazyLoading: true);
        using var guard = NPlusOneGuard.Begin();

        var total = 0;
        foreach (var customer in db.Customers.ToList())
        {
            total += customer.Orders.Count;
        }

        Assert.Equal(TestDatabase.CustomerCount * 3, total);
        var report = guard.Report;
        Assert.Equal(TestDatabase.CustomerCount, report.LazyLoadCount);
        var lazy = Assert.Single(report.Suspects, s => s.Kind == NPlusOneSuspectKind.LazyLoad);
        Assert.Equal("Customer.Orders", lazy.Navigation);
        Assert.Equal(TestDatabase.CustomerCount, lazy.Count);
        Assert.Null(lazy.Sql);
        Assert.StartsWith("GuardTests.Lazy_loading_in_a_loop_is_reported_with_the_navigation (GuardTests.cs:", lazy.CallSite, StringComparison.Ordinal);
        Assert.Single(report.Suspects, s => s.Kind == NPlusOneSuspectKind.Query);
        Assert.Contains("Lazy load of Customer.Orders ran 5 times", report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Lazy_loading_of_a_reference_navigation_names_the_dependent_entity()
    {
        using var db = _database.CreateContext(lazyLoading: true);
        using var guard = NPlusOneGuard.Begin();

        var names = db.Orders.Where(o => o.Id % 3 == 1).ToList().Select(o => o.Customer!.Name).ToList();

        Assert.Equal(TestDatabase.CustomerCount, names.Count);
        var lazy = Assert.Single(guard.Report.Suspects, s => s.Kind == NPlusOneSuspectKind.LazyLoad);
        Assert.Equal("Order.Customer", lazy.Navigation);
        Assert.StartsWith("GuardTests.Lazy_loading_of_a_reference_navigation_names_the_dependent_entity", lazy.CallSite, StringComparison.Ordinal);
    }

    [Fact]
    public void Lazy_loads_outside_a_scope_are_not_counted()
    {
        using var db = _database.CreateContext(lazyLoading: true);
        var customers = db.Customers.ToList();
        using var guard = NPlusOneGuard.Begin();
        guard.Dispose();

        foreach (var customer in customers)
        {
            _ = customer.Orders.Count;
        }

        Assert.Equal(0, guard.Report.LazyLoadCount);
        Assert.Equal(0, guard.Report.CommandCount);
    }

    [Fact]
    public async Task Parallel_scopes_do_not_see_each_others_commands()
    {
        using var start = new Barrier(2);

        var looping = Task.Run(async () =>
        {
            await using var db = _database.CreateContext();
            using var guard = NPlusOneGuard.Begin();
            start.SignalAndWait();
            for (var i = 1; i <= 10; i++)
            {
                _ = await db.Orders.Where(o => o.CustomerId == i).ToListAsync();
            }

            return guard.Report;
        });

        var single = Task.Run(async () =>
        {
            await using var db = _database.CreateContext();
            using var guard = NPlusOneGuard.Begin();
            start.SignalAndWait();
            _ = await db.Customers.Include(c => c.Orders).ToListAsync();
            return guard.Report;
        });

        var reports = await Task.WhenAll(looping, single);

        Assert.Equal(10, reports[0].CommandCount);
        Assert.Equal(10, Assert.Single(reports[0].Suspects).Count);
        Assert.Equal(1, reports[1].CommandCount);
        Assert.False(reports[1].HasSuspects);
    }

    [Fact]
    public async Task Concurrent_commands_in_one_scope_are_all_counted()
    {
        using var guard = NPlusOneGuard.Begin(o => o.CaptureCallSite = false);

        var tasks = Enumerable.Range(1, 20).Select(async i =>
        {
            await Task.Yield();
            await using var db = _database.CreateContext();
            return await db.Orders.Where(o => o.CustomerId == i).CountAsync();
        });
        await Task.WhenAll(tasks);

        Assert.Equal(20, guard.Report.CommandCount);
        Assert.Equal(20, Assert.Single(guard.Report.Suspects).Count);
    }

    [Fact]
    public async Task Nested_scope_records_into_both_and_restores_the_outer_scope()
    {
        await using var db = _database.CreateContext();
        using var outer = NPlusOneGuard.Begin();
        _ = await db.Customers.ToListAsync();

        using (var inner = NPlusOneGuard.Begin(o => o.RepeatThreshold = 10))
        {
            Assert.Same(inner, NPlusOneScope.Current);
            for (var i = 1; i <= 3; i++)
            {
                _ = await db.Orders.Where(o => o.CustomerId == i).ToListAsync();
            }

            Assert.Equal(3, inner.Report.CommandCount);
            Assert.False(inner.Report.HasSuspects);
        }

        Assert.Same(outer, NPlusOneScope.Current);
        _ = await db.Customers.ToListAsync();

        Assert.Equal(5, outer.Report.CommandCount);
        Assert.Equal(3, Assert.Single(outer.Report.Suspects).Count);
    }

    [Fact]
    public void Disposing_the_outer_scope_first_does_not_bring_it_back()
    {
        var outer = NPlusOneGuard.Begin();
        var inner = NPlusOneGuard.Begin();

        outer.Dispose();
        inner.Dispose();

        Assert.Null(NPlusOneScope.Current);
    }

    [Fact]
    public void Lazy_loads_of_a_context_without_the_guard_are_not_counted()
    {
        var options = new DbContextOptionsBuilder<ShopContext>().UseSqlite(_database.ConnectionString).UseLazyLoadingProxies().Options;
        using var db = new ShopContext(options);
        using var guard = NPlusOneGuard.Begin();

        foreach (var customer in db.Customers.ToList())
        {
            _ = customer.Orders.Count;
        }

        Assert.Equal(0, guard.Report.LazyLoadCount);
    }

    [Fact]
    public async Task A_pattern_that_throws_drops_the_record_not_the_query()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin(o => o.IgnoredSqlPatterns.Add(new Regex("(a+)+$", RegexOptions.None, TimeSpan.FromTicks(1))));

        var customers = await db.Customers.ToListAsync();

        Assert.Equal(TestDatabase.CustomerCount, customers.Count);
    }

    [Fact]
    public async Task Nothing_is_recorded_without_a_scope()
    {
        await using var db = _database.CreateContext();
        Assert.Null(NPlusOneScope.Current);

        for (var i = 1; i <= 5; i++)
        {
            _ = await db.Orders.Where(o => o.CustomerId == i).ToListAsync();
        }

        using var guard = NPlusOneGuard.Begin();
        Assert.Equal(0, guard.Report.CommandCount);
    }

    [Fact]
    public async Task Disposed_scope_stops_recording_but_keeps_its_report()
    {
        await using var db = _database.CreateContext();
        var guard = NPlusOneGuard.Begin();
        for (var i = 1; i <= 3; i++)
        {
            _ = await db.Orders.Where(o => o.CustomerId == i).ToListAsync();
        }

        guard.Dispose();
        _ = await db.Orders.Where(o => o.CustomerId == 4).ToListAsync();

        Assert.Null(NPlusOneScope.Current);
        Assert.Equal(3, guard.Report.CommandCount);
        Assert.Throws<NPlusOneViolationException>(guard.AssertNoNPlusOne);
    }

    [Fact]
    public async Task Ignored_patterns_are_not_recorded()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin(o => o.IgnoredSqlPatterns.Add(new Regex("FROM \"Orders\"")));

        for (var i = 1; i <= 5; i++)
        {
            _ = await db.Orders.Where(o => o.CustomerId == i).ToListAsync();
        }

        _ = await db.Customers.ToListAsync();

        Assert.Equal(1, guard.Report.CommandCount);
        Assert.False(guard.Report.HasSuspects);
    }

    [Fact]
    public async Task Repeat_threshold_is_configurable()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin(o => o.RepeatThreshold = 5);

        for (var i = 1; i <= 5; i++)
        {
            _ = await db.Orders.Where(o => o.CustomerId == i).ToListAsync();
        }

        Assert.False(guard.Report.HasSuspects);
        Assert.Equal(5, guard.Report.RepeatThreshold);
    }

    [Fact]
    public async Task Reports_never_contain_literal_values()
    {
        const string secret = "Zanzibar-7731-Secret";
        await using (var seed = _database.CreateContext())
        {
            seed.Customers.Add(new Customer { Name = secret });
            await seed.SaveChangesAsync();
        }

        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin();
        for (var i = 0; i < 3; i++)
        {
            _ = await db.Customers.FromSqlRaw($"SELECT * FROM \"Customers\" WHERE \"Name\" = '{secret}' AND \"Id\" > 77310 - 77310 /* {secret} */").ToListAsync();
            _ = await db.Customers.TagWith(secret).Where(c => c.Name == secret).ToListAsync();
        }

        var report = guard.Report;
        Assert.Equal(2, report.Suspects.Count);
        var exception = Assert.Throws<NPlusOneViolationException>(guard.AssertNoNPlusOne);
        foreach (var text in new[] { report.ToString(), exception.Message, string.Join("\n", report.Suspects.Select(s => s.Sql)) })
        {
            Assert.DoesNotContain("Zanzibar", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("7731", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Writes_are_ignored_by_default()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin();

        await SaveOrderPerCustomerAsync(db);

        Assert.Equal(1, guard.Report.CommandCount);
        Assert.False(guard.Report.HasSuspects);
    }

    [Fact]
    public async Task SaveChanges_per_element_is_reported_when_writes_are_included()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin(o => o.IncludeWrites = true);

        await SaveOrderPerCustomerAsync(db);

        var suspect = Assert.Single(guard.Report.Suspects);
        Assert.Equal(NPlusOneSuspectKind.Write, suspect.Kind);
        Assert.Equal(TestDatabase.CustomerCount, suspect.Count);
        Assert.StartsWith("INSERT INTO \"Orders\"", suspect.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("NEW-", guard.Report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calling_UseNPlusOneGuard_twice_does_not_double_count()
    {
        var options = new DbContextOptionsBuilder<ShopContext>()
            .UseSqlite(_database.ConnectionString)
            .UseNPlusOneGuard()
            .UseNPlusOneGuard()
            .Options;
        await using var db = new ShopContext(options);
        using var guard = NPlusOneGuard.Begin();

        _ = await db.Customers.ToListAsync();

        Assert.Equal(1, guard.Report.CommandCount);
    }

    [Fact]
    public async Task AssertNoNPlusOne_throws_with_a_readable_report()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin();
        foreach (var customer in await db.Customers.ToListAsync())
        {
            _ = await db.Orders.Where(o => o.CustomerId == customer.Id).ToListAsync();
        }

        var exception = Assert.Throws<NPlusOneViolationException>(guard.AssertNoNPlusOne);

        Assert.Same(exception.Report!.Suspects[0], exception.Report.Suspects[0]);
        Assert.StartsWith("N+1 guard found 1 suspect(s)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Query ran 5 times at GuardTests.AssertNoNPlusOne_throws_with_a_readable_report (GuardTests.cs:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WHERE \"o\".\"CustomerId\" = @p", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_reject_invalid_values()
    {
        var options = new NPlusOneOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.RepeatThreshold = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxSqlPreviewLength = 5);
        options.IgnoredSqlPatterns.Add(null!);
        Assert.Throws<ArgumentException>(() => NPlusOneGuard.Begin(options));
        Assert.Null(NPlusOneScope.Current);
    }

    [Fact]
    public async Task Long_sql_is_cut_to_the_preview_length()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin(o => o.MaxSqlPreviewLength = 20);
        for (var i = 1; i <= 3; i++)
        {
            _ = await db.Orders.Where(o => o.CustomerId == i).ToListAsync();
        }

        var sql = Assert.Single(guard.Report.Suspects).Sql!;
        Assert.Equal(23, sql.Length);
        Assert.EndsWith("...", sql, StringComparison.Ordinal);
    }

    private static async Task LoadOrdersAsync(ShopContext db, int customerId) =>
        _ = await db.Orders.Where(o => o.CustomerId == customerId).ToListAsync();

    private static async Task LoadOrdersAgainAsync(ShopContext db, int customerId) =>
        _ = await db.Orders.Where(o => o.CustomerId == customerId).ToListAsync();

    [Fact]
    public async Task One_SaveChanges_with_many_rows_is_one_write()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin(o => o.IncludeWrites = true);

        foreach (var customer in await db.Customers.AsNoTracking().ToListAsync())
        {
            db.Orders.Add(new Order { CustomerId = customer.Id, Number = $"NEW-{customer.Id}" });
        }

        await db.SaveChangesAsync();

        Assert.False(guard.Report.HasSuspects, guard.Report.ToString());
    }

    [Fact]
    public async Task The_transaction_SaveChanges_opens_is_part_of_its_write()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin(o => o.IncludeWrites = true);

        // Two rows per SaveChanges: EF Core wraps the statements in a transaction of its own.
        foreach (var customer in await db.Customers.AsNoTracking().ToListAsync())
        {
            db.Orders.Add(new Order { CustomerId = customer.Id, Number = $"NEW-A-{customer.Id}" });
            db.Orders.Add(new Order { CustomerId = customer.Id, Number = $"NEW-B-{customer.Id}" });
            await db.SaveChangesAsync();
        }

        var suspect = Assert.Single(guard.Report.Suspects);
        Assert.StartsWith("INSERT INTO \"Orders\"", suspect.Sql, StringComparison.Ordinal);
        Assert.Equal(TestDatabase.CustomerCount, suspect.Count);
    }

    [Fact]
    public async Task A_cancelled_SaveChanges_does_not_hide_later_transactions()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin(o => o.IncludeWrites = true);
        db.Orders.Add(new Order { CustomerId = 1, Number = "NEW-CANCELLED" });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(cancelled.Token));
        foreach (var customer in await db.Customers.AsNoTracking().ToListAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await transaction.CommitAsync();
        }

        Assert.Equal(TestDatabase.CustomerCount, Assert.Single(guard.Report.Suspects).Count);
    }

    [Fact]
    public async Task A_transaction_per_element_is_reported_when_writes_are_included()
    {
        await using var db = _database.CreateContext();
        using var guard = NPlusOneGuard.Begin(o => o.IncludeWrites = true);

        foreach (var customer in await db.Customers.AsNoTracking().ToListAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await transaction.CommitAsync();
        }

        var suspect = Assert.Single(guard.Report.Suspects);
        Assert.Equal(NPlusOneSuspectKind.Write, suspect.Kind);
        Assert.Equal(TestDatabase.CustomerCount, suspect.Count);
        Assert.Equal("BEGIN TRANSACTION", suspect.Sql);
    }

    private static async Task SaveOrderPerCustomerAsync(ShopContext db)
    {
        foreach (var customer in await db.Customers.AsNoTracking().ToListAsync())
        {
            db.Orders.Add(new Order { CustomerId = customer.Id, Number = $"NEW-{customer.Id}" });
            await db.SaveChangesAsync();
        }
    }
}
