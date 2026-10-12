using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

// A service called in a query's final projection runs on the client once per row.
public class OrderCounter(IDbContextFactory<AppDb> factory)
{
    public int Count(int customerId)
    {
        using var db = factory.CreateDbContext();
        return db.Orders.Count(o => o.CustomerId == customerId);
    }

    public static string Label(int id) => "#" + id;
}

public class SameContextCounter(AppDb db)
{
    public int Count(int customerId) => db.Orders.Count(o => o.CustomerId == customerId);
}

public class ClientProjectionQueries(AppDb db, OrderCounter counter, SameContextCounter sameContext)
{
    public async Task<int> ServiceQueries()
    {
        // ground-truth: the harness shares one in-memory SQLite connection, where a second context cannot query while the outer statement is open
        var rows = await db.Customers.Select(c => new { c.Id, Orders = counter.Count(c.Id) }).ToListAsync(); // expect: NP0001
        return rows.Count;
    }


    public List<int> SameContext() => db.Customers.Select(c => sameContext.Count(c.Id)).ToList(); // expect: NP0001

    public List<string> NoQuery() => db.Customers.Select(c => OrderCounter.Label(c.Id)).ToList();

    // Only the final projection runs on the client; a method in Where would make EF Core refuse the query.
    public List<Customer> InAFilter() => db.Customers.Where(c => sameContext.Count(c.Id) > 0).ToList();

    public List<string> AfterTheProjection() => db.Customers.Select(c => OrderCounter.Label(c.Id)).Where(l => l.Length > 0).ToList();
}
