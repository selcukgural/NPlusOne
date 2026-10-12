using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

// Queries inside loop statements.
public class Loops
{
    private readonly AppDb _db;
    public Loops(AppDb db) => _db = db;

    public async Task ForeachWithLocalContext(AppDb db)
    {
        var customers = await db.Customers.ToListAsync();
        foreach (var c in customers)
        {
            var orders = await db.Orders.Where(o => o.CustomerId == c.Id).ToListAsync(); // expect: NP0001
        }
    }

    public async Task ForeachWithFieldContext()
    {
        var customers = await _db.Customers.ToListAsync();
        foreach (var c in customers)
        {
            var orders = await _db.Orders.Where(o => o.CustomerId == c.Id).ToListAsync(); // expect: NP0001
        }
    }

    public async Task ForWithFind(int[] ids)
    {
        for (var i = 0; i < ids.Length; i++)
        {
            var order = await _db.Orders.FindAsync(ids[i]); // expect: NP0001
        }
    }

    public void ForeachWithContextFind(int[] ids)
    {
        foreach (var id in ids)
        {
            var order = _db.Find<Order>(id); // expect: NP0001
        }
    }

    public async Task WhileQueue(Queue<int> queue)
    {
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            var any = await _db.Orders.AnyAsync(o => o.CustomerId == id); // expect: NP0001
        }
    }

    public void DoWhile(Stack<int> stack)
    {
        do
        {
            var id = stack.Pop();
            var count = _db.Orders.Count(o => o.CustomerId == id); // expect: NP0001
        }
        while (stack.Count > 0);
    }

    public async Task AwaitForeach(IAsyncEnumerable<int> ids)
    {
        await foreach (var id in ids)
        {
            var sum = await _db.Orders.Where(o => o.CustomerId == id).SumAsync(o => o.Total); // expect: NP0001
        }
    }

    public void ForeachOverQueryableInsideLoop(int[] ids)
    {
        foreach (var id in ids)
        {
            foreach (var order in _db.Orders.Where(o => o.CustomerId == id)) // expect: NP0001
            {
            }
        }
    }

    public void SyncMaterializerOnQueryable(int[] ids)
    {
        foreach (var id in ids)
        {
            var list = _db.Orders.Where(o => o.CustomerId == id).ToList(); // expect: NP0001
        }
    }

    public async Task QueryThroughLocalVariable(int[] ids)
    {
        foreach (var id in ids)
        {
            var query = _db.Orders.Where(o => o.CustomerId == id);
            var list = await query.ToListAsync(); // expect: NP0001
        }
    }

    public async Task QueryFromSetMethod(int[] ids)
    {
        foreach (var id in ids)
        {
            var c = await _db.Set<Customer>().FirstOrDefaultAsync(x => x.Id == id); // expect: NP0001
        }
    }

    public async Task QueryThroughQueryProperty(int[] ids)
    {
        foreach (var id in ids)
        {
            var big = await _db.BigOrders.CountAsync(o => o.CustomerId == id); // expect: NP0001
        }
    }

    public async Task ExplicitLoading()
    {
        var orders = await _db.Orders.ToListAsync();
        foreach (var o in orders)
        {
            await _db.Entry(o).Reference(x => x.Customer).LoadAsync(); // expect: NP0001
            await _db.Entry(o).Collection(x => x.Lines).Query().Where(l => l.Price > 0).LoadAsync(); // expect: NP0001
        }
    }

    public async Task RawSql(int[] ids)
    {
        foreach (var id in ids)
        {
            await _db.Database.ExecuteSqlAsync($"UPDATE Orders SET Total = 0 WHERE Id = {id}"); // expect: NP0001
            var rows = await _db.Orders.FromSqlInterpolated($"SELECT * FROM Orders WHERE Id = {id}").ToListAsync(); // expect: NP0001
        }
    }

    public async Task<Order> EarlyExitStillWalksTheArray(int[] ids)
    {
        for (var i = 0; i < ids.Length; i++)
        {
            var order = await _db.Orders.FindAsync(ids[i]); // expect: NP0001
            if (order is null)
            {
                return null;
            }
        }

        return null;
    }

    public async Task NestedLoopsReportOnce(int[] ids, int[] other)
    {
        foreach (var id in ids)
        {
            foreach (var x in other)
            {
                var n = await _db.Orders.CountAsync(o => o.CustomerId == id + x); // expect: NP0001
            }
        }
    }

    // DbSet<T> has its own AsAsyncEnumerable instance method, which streams the set's query.
    public async Task SetStreamedPerElement(int[] ids)
    {
        foreach (var id in ids)
        {
            await foreach (var order in _db.Orders.AsAsyncEnumerable()) // expect: NP0001
            {
            }
        }
    }

    public async Task SetAsQueryablePerElement(int[] ids)
    {
        foreach (var id in ids)
        {
            var n = await _db.Orders.AsQueryable().CountAsync(o => o.CustomerId == id); // expect: NP0001
        }
    }

    public async Task SetStreamedOnce()
    {
        await foreach (var order in _db.Orders.AsAsyncEnumerable())
        {
        }
    }
}
