using Microsoft.EntityFrameworkCore;

namespace Corpus.Lazy;

// EF Core evaluates a method called in a query's final projection on the client, once per row, with the
// materialized entity: what the method reads on it is read once per row.
public class OrderFormatter
{
    public string Describe(Order order) => order.Customer.Name + " " + order.Total;
}

public class ClientProjection(AppDb db, OrderFormatter formatter)
{
    private static string Describe(Order order) => order.Customer.Name + " " + order.Total;

    private static string Amount(Order order) => order.Total.ToString("0.00");

    private static string Format(decimal total) => total.ToString("0.00");

    public Task<List<string>> StaticMethod() => db.Orders.Select(o => Describe(o)).ToListAsync(); // expect: NP0002

    public Task<List<string>> StaticMethodIncluded() => db.Orders.Include(o => o.Customer).Select(o => Describe(o)).ToListAsync();

    public async Task<int> InAnonymousObject()
    {
        var rows = await db.Orders.Select(o => new { o.Id, Text = Describe(o) }).ToListAsync(); // expect: NP0002
        return rows.Count;
    }

    public Task<List<string>> ServiceMethod() => db.Orders.Select(o => formatter.Describe(o)).ToListAsync(); // expect: NP0002

    public Task<List<string>> ScalarsOnly() => db.Orders.Select(o => Amount(o)).ToListAsync();

    public Task<List<string>> ScalarArgument() => db.Orders.Select(o => Format(o.Total)).ToListAsync();

    public Task<List<string>> Untracked() => db.Orders.AsNoTracking().Select(o => Describe(o)).ToListAsync(); // expect: NP0002
}
