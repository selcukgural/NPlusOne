using Microsoft.EntityFrameworkCore;

namespace Corpus.Lazy;

// Locals that get their elements from an assignment after the declaration: what every assignment gives them decides
// where the elements come from.
public class AssignedLocals
{
    private readonly AppDb _db;

    public AssignedLocals(AppDb db) => _db = db;

    public async Task<int> AssignedAfterDeclaration()
    {
        List<Order> orders;
        orders = await _db.Orders.ToListAsync();
        var length = 0;
        foreach (var order in orders)
        {
            length += order.Customer.Name.Length; // expect: NP0002
        }

        return length;
    }

    public async Task<int> AssignedWhenMissing()
    {
        List<Order> orders = null;
        orders ??= await _db.Orders.ToListAsync();
        var length = 0;
        foreach (var order in orders)
        {
            length += order.Customer.Name.Length; // expect: NP0002
        }

        return length;
    }

    public int AssignedFromCacheWhenMissing()
    {
        List<Order> orders = null;
        orders ??= Cached();
        var length = 0;
        foreach (var order in orders)
        {
            length += order.Customer.Name.Length;
        }

        return length;
    }

    // A deconstruction gives the local elements the analysis does not follow, so it cannot tell where they come from and
    // stays quiet, even though a later assignment would give it a query's. Here the cache always has them.
    public async Task<int> DeconstructedThenQueried()
    {
        var (orders, source) = (Cached(), "cache");
        if (orders.Count == 0)
        {
            orders = await _db.Orders.ToListAsync();
            source = "database";
        }

        var length = source.Length;
        foreach (var order in orders)
        {
            length += order.Customer.Name.Length;
        }

        return length;
    }

    private static List<Order> Cached() => [new Order { Customer = new Customer { Name = "cached" } }];
}
