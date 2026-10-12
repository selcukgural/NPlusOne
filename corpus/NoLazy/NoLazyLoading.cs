using Microsoft.EntityFrameworkCore;

namespace Corpus.NoLazy;

// Lazy loading is off in this folder: reading a navigation never runs a query, so NP0002 stays
// quiet. Queries in loops are still reported.
public class NoLazyLoading
{
    private readonly AppDb _db;
    public NoLazyLoading(AppDb db) => _db = db;

    public async Task NavigationReadsAreNotQueries()
    {
        var orders = await _db.Orders.ToListAsync();
        foreach (var o in orders)
        {
            Console.WriteLine(o.Customer?.Name);
        }

        var names = orders.Select(Mapper.CustomerName).ToList();
    }

    public async Task QueriesInLoopsAreStillReported(List<int> ids)
    {
        foreach (var id in ids)
        {
            var o = await _db.Orders.FindAsync(id); // expect: NP0001
        }
    }

    [HttpGetLike]
    public async Task<List<Order>> EndpointLikeMethod() => await _db.Orders.ToListAsync();
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class HttpGetLikeAttribute : Attribute;
