using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

// Queries reached through delegates kept in locals, query fields and properties set once, and
// methods that recurse through each other.
public class StoredAndShared
{
    private readonly AppDb _db;
    private readonly IQueryable<Order> _bigOrders;
    private readonly IQueryable<Order> _inMemory;
    private readonly IQueryable<Customer> _named;

    public StoredAndShared(AppDb db, List<Order> cached)
    {
        _db = db;
        _bigOrders = db.Orders.Where(o => o.Total > 100);
        _inMemory = cached.AsQueryable();
        _named = db.Customers.Where(c => c.Name != null);
        Recent = db.Orders.OrderByDescending(o => o.Id).Take(50);
    }

    public IQueryable<Order> Recent { get; }

    public async Task StoredLambdaCalledInLoop(List<int> ids)
    {
        Func<int, Task<int>> count = id => _db.Orders.CountAsync(o => o.CustomerId == id);
        foreach (var id in ids)
        {
            await count(id); // expect: NP0001
        }
    }

    public void StoredMethodGroupCalledInLoop(List<int> ids)
    {
        Func<int, Order> find = Find;
        foreach (var id in ids)
        {
            find(id); // expect: NP0001
        }
    }

    private Order Find(int id) => _db.Orders.Find(id);

    public async Task StoredLambdaCalledOnce(int id)
    {
        Func<int, Task<int>> count = x => _db.Orders.CountAsync(o => o.CustomerId == x);
        await count(id);
    }

    public void StoredLambdaWithoutQuery(List<int> ids)
    {
        Func<int, int> twice = x => x * 2;
        foreach (var id in ids)
        {
            twice(id);
        }
    }

    public async Task QueryFieldFromConstructor(List<int> ids)
    {
        foreach (var id in ids)
        {
            var n = await _bigOrders.CountAsync(o => o.CustomerId == id); // expect: NP0001
            var c = await _named.FirstOrDefaultAsync(x => x.Id == id); // expect: NP0001
        }
    }

    public void InMemoryQueryField(List<int> ids)
    {
        foreach (var id in ids)
        {
            var n = _inMemory.Count(o => o.CustomerId == id);
        }
    }

    public async Task QueryPropertyFromConstructor(List<int> ids)
    {
        foreach (var id in ids)
        {
            var any = await Recent.AnyAsync(o => o.CustomerId == id); // expect: NP0001
        }
    }

    public async Task<int> Depth(int categoryId) => await ParentDepth(categoryId);

    private async Task<int> ParentDepth(int categoryId)
    {
        var category = await _db.Categories.FirstAsync(c => c.Id == categoryId);
        return category.ParentId is null ? 0 : 1 + await Depth(category.ParentId.Value); // expect: NP0004
    }

    public int Ping(int n) => n <= 0 ? 0 : Pong(n - 1);

    private int Pong(int n) => n <= 0 ? 0 : Ping(n - 1);
}
