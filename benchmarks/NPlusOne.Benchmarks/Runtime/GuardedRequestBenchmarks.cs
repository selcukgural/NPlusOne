using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NPlusOne.EntityFrameworkCore;

namespace NPlusOne.Benchmarks.Runtime;

/// <summary>
/// A request's database work end to end on SQLite in memory, without the guard, inside a scope, and inside a scope that
/// captures call sites (the default): the difference between the rows is what the guard adds to a request.
/// </summary>
#pragma warning disable CA1001 // BenchmarkDotNet needs an unsealed class; GlobalCleanup closes the connection.
public class GuardedRequestBenchmarks
#pragma warning restore CA1001
{
    /// <summary>How many customers, each with one order, the database holds; also the loop count of each request.</summary>
    private const int Rows = 10;

    /// <summary>One open in-memory database, shared by every context of the run.</summary>
    private SqliteConnection _connection = null!;

    /// <summary>The options of every context: SQLite, lazy-loading proxies and the guard.</summary>
    private DbContextOptions<ShopContext> _options = null!;

    /// <summary>The guard's options for the scoped modes.</summary>
    private NPlusOneOptions _guard = null!;

    /// <summary>Whether a scope is open, and whether it captures call sites.</summary>
    [Params(GuardMode.Off, GuardMode.Scope, GuardMode.ScopeWithCallSite)]
    public GuardMode Mode { get; set; }

    /// <summary>Creates and seeds the database.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ShopContext>().UseSqlite(_connection).UseLazyLoadingProxies().UseNPlusOneGuard().Options;
        _guard = new NPlusOneOptions { CaptureCallSite = Mode == GuardMode.ScopeWithCallSite };

        using var db = new ShopContext(_options);
        db.Database.EnsureCreated();
        for (var i = 1; i <= Rows; i++)
        {
            db.Customers.Add(new Customer { Name = $"Customer {i}", Orders = [new Order { Number = $"N-{i}" }] });
        }

        db.SaveChanges();
    }

    /// <summary>Closes the database.</summary>
    [GlobalCleanup]
    public void Cleanup() => _connection.Dispose();

    /// <summary>A request that looks up one order per customer: ten queries with the same SQL.</summary>
    /// <returns>How many orders were found.</returns>
    [Benchmark]
    public int Queries()
    {
        using var scope = Begin();
        using var db = new ShopContext(_options);
        var found = 0;
        for (var id = 1; id <= Rows; id++)
        {
            found += db.Orders.AsNoTracking().Count(o => o.CustomerId == id);
        }

        return found;
    }

    /// <summary>A request that reads the customer of each order through a lazy-loading proxy: one query, ten lazy loads.</summary>
    /// <returns>The total length of the customer names.</returns>
    [Benchmark]
    public int LazyLoads()
    {
        using var scope = Begin();
        using var db = new ShopContext(_options);
        var length = 0;
        foreach (var order in db.Orders.ToList())
        {
            length += order.Customer!.Name.Length;
        }

        return length;
    }

    /// <summary>Begins the scope the mode asks for.</summary>
    /// <returns>The scope, or <see langword="null"/> without the guard.</returns>
    private NPlusOneScope? Begin() => Mode == GuardMode.Off ? null : NPlusOneGuard.Begin(_guard);

    /// <summary>How a request runs.</summary>
    public enum GuardMode
    {
        /// <summary>No scope: the guard's hooks return after one async-local read.</summary>
        Off,

        /// <summary>A scope without call sites.</summary>
        Scope,

        /// <summary>A scope with call sites, the default.</summary>
        ScopeWithCallSite,
    }
}

/// <summary>A customer with orders.</summary>
public class Customer
{
    /// <summary>The key.</summary>
    public int Id { get; set; }

    /// <summary>The name, read through the lazy-loaded navigation.</summary>
    public string Name { get; set; } = "";

    /// <summary>The customer's orders.</summary>
    public virtual ICollection<Order> Orders { get; set; } = [];
}

/// <summary>An order of a customer.</summary>
public class Order
{
    /// <summary>The key.</summary>
    public int Id { get; set; }

    /// <summary>A number, so the row has a value besides its keys.</summary>
    public string Number { get; set; } = "";

    /// <summary>The foreign key the queries filter on.</summary>
    public int CustomerId { get; set; }

    /// <summary>The navigation the lazy loads read.</summary>
    public virtual Customer? Customer { get; set; }
}

/// <summary>The benchmark's context.</summary>
/// <param name="options">SQLite, proxies and the guard.</param>
public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    /// <summary>The customers.</summary>
    public DbSet<Customer> Customers => Set<Customer>();

    /// <summary>The orders.</summary>
    public DbSet<Order> Orders => Set<Order>();
}
