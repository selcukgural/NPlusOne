using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NPlusOne.EntityFrameworkCore.Tests;

/// One private in-memory SQLite database per test (5 customers with 3 orders each). Each context gets
/// its own connection to the shared-cache database, so contexts can run commands concurrently.
public sealed class TestDatabase : IDisposable
{
    public const int CustomerCount = 5;

    private readonly string _connectionString = $"Data Source=nplusone-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    // Keeps the in-memory database alive for the lifetime of the test.
    private readonly SqliteConnection _keepAlive;

    public TestDatabase()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        for (var c = 1; c <= CustomerCount; c++)
        {
            var customer = new Customer { Name = $"Customer {c}" };
            for (var o = 1; o <= 3; o++)
            {
                customer.Orders.Add(new Order { Number = $"ORD-{c}-{o}" });
            }

            db.Customers.Add(customer);
        }

        db.SaveChanges();
    }

    public string ConnectionString => _connectionString;

    public ShopContext CreateContext(bool lazyLoading = false)
    {
        var builder = new DbContextOptionsBuilder<ShopContext>().UseSqlite(_connectionString).UseNPlusOneGuard();
        if (lazyLoading)
        {
            builder.UseLazyLoadingProxies();
        }

        return new ShopContext(builder.Options);
    }

    public void Dispose() => _keepAlive.Dispose();
}
