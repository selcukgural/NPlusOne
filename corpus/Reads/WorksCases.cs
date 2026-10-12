using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Corpus.Reads;

// The catalogue's "works" rows, each kept here as a case: less common operators and APIs, LINQ shapes, call shapes
// and run-time dispatch that already reach the round trip, and the silent shapes next to them.
public interface ILineCounter
{
    int Count(int orderId);

    // A default interface method calling an implemented member.
    int CountTwice(int orderId) => Count(orderId) * 2;
}

public class LineCounter(AppDb db) : ILineCounter
{
    public int Count(int orderId) => db.OrderLines.Count(l => l.OrderId == orderId);
}

public interface IRepository<T> where T : class
{
    Task<T> GetByIdAsync(int id);
}

public class Repository<T>(AppDb db) : IRepository<T> where T : class
{
    public async Task<T> GetByIdAsync(int id) => await db.Set<T>().FindAsync(id);
}

public class OrderRepo(AppDb db) : Repository<Order>(db);

public static class CustomerExtensions
{
    public static int OrderCount(this Customer customer, AppDb db) => db.Orders.Count(o => o.CustomerId == customer.Id);
}

public class WorksCases(AppDb db, ILineCounter counter, IRepository<Order> repository, IServiceProvider services)
{
    public async Task<int> SyncOperators()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += db.Orders.All(o => o.CustomerId != customer.Id) ? 1 : 0; // expect: NP0001
            total += db.Orders.Select(o => o.CustomerId).Contains(customer.Id) ? 1 : 0; // expect: NP0001
            total += (int)db.Orders.LongCount(o => o.CustomerId == customer.Id); // expect: NP0001
            total += (int)(db.Orders.Where(o => o.CustomerId == customer.Id).Max(o => (decimal?)o.Total) ?? 0); // expect: NP0001
            total += (int)(db.Orders.Where(o => o.CustomerId == customer.Id).Average(o => (decimal?)o.Total) ?? 0); // expect: NP0001
            total += db.Orders.OrderBy(o => o.Id).LastOrDefault(o => o.CustomerId == customer.Id)?.Id ?? 0; // expect: NP0001
            total += db.Orders.OrderBy(o => o.Id).Where(o => o.CustomerId == customer.Id).ElementAtOrDefault(0)?.Id ?? 0; // expect: NP0001
            total += db.Orders.Where(o => o.CustomerId == customer.Id).ToArray().Length; // expect: NP0001
        }

        return total;
    }

    public async Task<int> AsyncOperators()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += await db.Orders.AllAsync(o => o.CustomerId != customer.Id) ? 1 : 0; // expect: NP0001
            total += await db.Orders.Select(o => o.CustomerId).ContainsAsync(customer.Id) ? 1 : 0; // expect: NP0001
            total += (int)await db.Orders.LongCountAsync(o => o.CustomerId == customer.Id); // expect: NP0001
            total += (int)(await db.Orders.Where(o => o.CustomerId == customer.Id).MaxAsync(o => (decimal?)o.Total) ?? 0); // expect: NP0001
            total += (await db.Orders.Where(o => o.CustomerId == customer.Id).ToDictionaryAsync(o => o.Id)).Count; // expect: NP0001
#if NET9_0_OR_GREATER
            total += (await db.Orders.Where(o => o.CustomerId == customer.Id).ToHashSetAsync()).Count; // expect: NP0001
#endif
            total += (await db.Orders.SingleOrDefaultAsync(o => o.Id == customer.Id))?.Id ?? 0; // expect: NP0001
            total += (await db.Orders.Where(o => o.CustomerId == customer.Id).ToArrayAsync()).Length; // expect: NP0001
        }

        return total;
    }

    public async Task SetBasedPerElement()
    {
        foreach (var id in await db.Orders.Select(o => o.Id).ToListAsync())
        {
            await db.Orders.Where(o => o.Id == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Total, o => o.Total + 1)); // expect: NP0001
            await db.OrderLines.Where(l => l.OrderId == id && l.Price < 0).ExecuteDeleteAsync(); // expect: NP0001
        }
    }

    public async Task<int> RawSqlPerElement()
    {
        var total = 0;
        foreach (var id in await db.Customers.Select(c => c.Id).ToListAsync())
        {
            total += db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM Orders WHERE CustomerId = {id}").Single(); // expect: NP0001
            total += await db.Database.ExecuteSqlRawAsync("UPDATE Orders SET Total = Total WHERE CustomerId = {0}", id); // expect: NP0001
            total += db.Orders.FromSqlRaw("SELECT * FROM Orders WHERE CustomerId = {0}", id).ToList().Count; // expect: NP0001
        }

        return total;
    }

    public async Task<int> EntryRoundTrips()
    {
        var total = 0;
        foreach (var order in await db.Orders.ToListAsync())
        {
            total += await db.Entry(order).Collection(o => o.Lines).Query().CountAsync(); // expect: NP0001
            await db.Entry(order).ReloadAsync(); // expect: NP0001
            total += (await db.Entry(order).GetDatabaseValuesAsync()) is null ? 0 : 1; // expect: NP0001
        }

        return total;
    }

    public async Task<int> EntryStateOnly()
    {
        var total = 0;
        foreach (var order in await db.Orders.ToListAsync())
        {
            total += db.Entry(order).Collection(o => o.Lines).IsLoaded ? 1 : 0;
            db.Entry(order).State = EntityState.Unchanged;
        }

        return total;
    }

    public async Task<int> LocalLookups()
    {
        var orders = await db.Orders.ToListAsync();
        var total = 0;
        foreach (var order in orders)
        {
            total += db.Orders.Local.FirstOrDefault(o => o.Id == order.Id)?.Id ?? 0;
        }

        return total;
    }

    public async Task<int> QueryTypedAsEnumerable()
    {
        IEnumerable<Order> big = db.Orders.Where(o => o.Total >= 0);
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += big.Count(o => o.CustomerId == customer.Id); // expect: NP0001
        }

        return total;
    }

    public async Task<int> AsEnumerablePerElement()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += db.Orders.AsEnumerable().Where(o => o.CustomerId == customer.Id).ToList().Count; // expect: NP0001
        }

        return total;
    }

    public async Task<int> FiltersAndTags()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += db.Orders.IgnoreQueryFilters().TagWith("per customer").Count(o => o.CustomerId == customer.Id); // expect: NP0001
        }

        return total;
    }

    public async Task<int> MoreLambdas()
    {
        var customers = await db.Customers.ToListAsync();
        var total = customers.Sum(c => db.Orders.Count(o => o.CustomerId == c.Id)); // expect: NP0001
        total += customers.OrderBy(c => db.Orders.Count(o => o.CustomerId == c.Id)).First().Id; // expect: NP0001
        total += customers.All(c => db.Orders.Any(o => o.CustomerId == c.Id)) ? 1 : 0; // expect: NP0001
        total += customers.Aggregate(0, (sum, c) => sum + db.Orders.Count(o => o.CustomerId == c.Id)); // expect: NP0001
        total += customers.ToLookup(c => c.Id, c => db.Orders.Count(o => o.CustomerId == c.Id)).Count; // expect: NP0001
        total += customers.Max(c => db.Orders.Count(o => o.CustomerId == c.Id)); // expect: NP0001
        total += string.Join(",", customers.Select(c => db.Orders.Count(o => o.CustomerId == c.Id))).Length; // expect: NP0001
        return total;
    }

    public async Task<int> QuerySyntax()
    {
        var customers = await db.Customers.ToListAsync();
        var counts = from c in customers
                     let n = db.Orders.Count(o => o.CustomerId == c.Id) // expect: NP0001
                     select n;
        return counts.Sum();
    }

    public async Task<int> ExtensionMethodPerElement()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += customer.OrderCount(db); // expect: NP0001
        }

        return total;
    }

    private int CountOf<T>() where T : class => db.Set<T>().Count();

    public async Task<int> GenericMethodPerElement()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += CountOf<Order>() + customer.Id; // expect: NP0001
        }

        return total;
    }

    public async Task<int> DefaultInterfaceMethodPerElement()
    {
        var total = 0;
        foreach (var order in await db.Orders.ToListAsync())
        {
            total += counter.CountTwice(order.Id); // expect: NP0001
        }

        return total;
    }

    public async Task<int> GenericRepositoryPerElement()
    {
        var total = 0;
        foreach (var id in await db.Orders.Select(o => o.Id).ToListAsync())
        {
            total += (await repository.GetByIdAsync(id))?.Id ?? 0; // expect: NP0001
        }

        return total;
    }

    public async Task<int> CachedPerElement()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += cache.GetOrCreate("orders", _ => db.Orders.Count()) + customer.Id;
        }

        return total;
    }

    // A key per element misses the cache for every element the first time: one query each.
    public async Task<int> CachedPerElementKey()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += cache.GetOrCreate($"orders-{customer.Id}", _ => db.Orders.Count(o => o.CustomerId == customer.Id)); // expect: NP0001
        }

        return total;
    }

    public async Task<int> ResolvedPerElement()
    {
        var total = 0;
        foreach (var order in await db.Orders.ToListAsync())
        {
            total += services.GetRequiredService<ILineCounter>().Count(order.Id); // expect: NP0001
        }

        return total;
    }

    public async Task<int> ActivatedPerElement()
    {
        var total = 0;
        foreach (var order in await db.Orders.ToListAsync())
        {
            total += ((ILineCounter)Activator.CreateInstance(typeof(LineCounter), db)!).Count(order.Id); // expect: NP0001
        }

        return total;
    }

    public async Task<int> ExpressionBuiltAtRunTime()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            var parameter = Expression.Parameter(typeof(Order), "o");
            var body = Expression.Equal(Expression.Property(parameter, nameof(Order.CustomerId)), Expression.Constant(customer.Id));
            total += db.Orders.Where(Expression.Lambda<Func<Order, bool>>(body, parameter)).ToList().Count; // expect: NP0001
        }

        return total;
    }

    public int ShadowStyleProperty() => db.Orders.Where(o => EF.Property<decimal>(o, nameof(Order.Total)) >= 0).ToList().Count;
}
