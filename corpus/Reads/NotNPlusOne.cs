using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

// Code that looks like an N+1 but is not one. None of these lines may produce a diagnostic.
public class NotNPlusOne
{
    private readonly AppDb _db;
    private List<Product> _products;
    public NotNPlusOne(AppDb db) => _db = db;

    public async Task QueryBeforeTheLoop(List<int> ids)
    {
        var orders = await _db.Orders.Where(o => ids.Contains(o.CustomerId)).ToListAsync();
        var byCustomer = orders.ToLookup(o => o.CustomerId);
        foreach (var id in ids)
        {
            var mine = byCustomer[id].ToList();
        }
    }

    public async Task ForeachCollectionRunsOnce()
    {
        foreach (var order in await _db.Orders.ToListAsync())
        {
            Console.WriteLine(order.Total);
        }

        foreach (var order in _db.Orders.Where(o => o.Total > 0))
        {
            Console.WriteLine(order.Total);
        }
    }

    public List<int> SubqueryInsideExpressionTree()
        => _db.Customers.Select(c => _db.Orders.Count(o => o.CustomerId == c.Id)).ToList();

    public List<decimal> CorrelatedCollectionInsideExpressionTree()
        => _db.Customers.Select(c => c.Orders.Sum(o => o.Total)).ToList();

    public async Task ChunkedBatches(List<int> ids)
    {
        foreach (var batch in ids.Chunk(500))
        {
            var orders = await _db.Orders.Where(o => batch.Contains(o.CustomerId)).ToListAsync();
        }
    }

    public async Task KeysetPaging()
    {
        var lastId = 0;
        while (true)
        {
            var page = await _db.Orders.Where(o => o.Id > lastId).OrderBy(o => o.Id).Take(100).ToListAsync();
            if (page.Count == 0)
            {
                break;
            }

            lastId = page[^1].Id;
        }
    }

    public async Task OffsetPaging()
    {
        for (var skip = 0; ; skip += 100)
        {
            var page = await _db.Orders.OrderBy(o => o.Id).Skip(skip).Take(100).ToListAsync();
            if (page.Count == 0)
            {
                break;
            }
        }
    }

    public async Task Polling(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (await _db.Orders.AnyAsync(o => o.Total < 0, token))
            {
                return;
            }

            await Task.Delay(1000, token);
        }
    }

    public void LoadOnceGuardWithCoalesce(List<int> ids)
    {
        foreach (var id in ids)
        {
            _products ??= _db.Products.ToList();
        }
    }

    public void LoadOnceGuardWithNullCheck(List<int> ids)
    {
        foreach (var id in ids)
        {
            if (_products is null)
            {
                _products = _db.Products.ToList();
            }
        }
    }

    public void InMemoryQueryable(List<Order> orders, List<int> ids)
    {
        var source = orders.AsQueryable();
        foreach (var id in ids)
        {
            var n = source.Count(o => o.CustomerId == id);
        }
    }

    public void LinqToObjectsInLoop(List<Order> orders, List<int> ids)
    {
        foreach (var id in ids)
        {
            var mine = orders.Where(o => o.CustomerId == id).ToList();
        }
    }

    public void QueryBuiltButNotExecutedInLoop(List<int> ids)
    {
        var query = _db.Orders.AsQueryable();
        foreach (var id in ids)
        {
            query = query.Where(o => o.CustomerId != id);
        }

        var result = query.ToList();
    }

    public async Task SingleQueryOutsideAnyLoop(int id)
    {
        var order = await _db.Orders.FindAsync(id);
        var count = await _db.Orders.CountAsync();
    }

    public async Task<string> RetryUntilUnique()
    {
        const int maxAttempts = 10;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var code = Guid.NewGuid().ToString("N")[..8];
            var exists = await _db.Customers.AnyAsync(c => c.Name == code);
            if (!exists)
            {
                return code;
            }
        }

        throw new InvalidOperationException("No unique code.");
    }

    public async Task<string> RetryInLoopCondition()
    {
        string code;
        do
        {
            code = Guid.NewGuid().ToString("N")[..8];
        }
        while (await _db.Customers.AnyAsync(c => c.Name == code));

        return code;
    }

    public async Task<int> SearchWithBreak()
    {
        var candidate = 0;
        while (true)
        {
            candidate = Random.Shared.Next();
            if (!await _db.Orders.AnyAsync(o => o.Id == candidate))
            {
                break;
            }
        }

        return candidate;
    }

    public void LambdaNotRunPerElement(int id)
    {
        Func<int> count = () => _db.Orders.Count(o => o.CustomerId == id);
        Lazy<List<Order>> lazy = new(() => _db.Orders.ToList());
    }
}
