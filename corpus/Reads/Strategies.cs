using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

// Loops over a set of different implementations (checks, plugins, strategies): each element is its
// own object, so a query in one implementation runs once, not once per element.
public interface IHealthCheck
{
    Task<bool> RunAsync();
}

public class DatabaseHealthCheck(AppDb db) : IHealthCheck
{
    public async Task<bool> RunAsync() => await db.Orders.AnyAsync();
}

public class DiskHealthCheck : IHealthCheck
{
    public Task<bool> RunAsync() => Task.FromResult(true);
}

public interface IOrderLoader
{
    Task<int> CountAsync(int customerId);
}

public class ActiveOrderLoader(AppDb db) : IOrderLoader
{
    public Task<int> CountAsync(int customerId) => db.Orders.CountAsync(o => o.CustomerId == customerId && o.Total > 0);
}

public class AllOrderLoader(AppDb db) : IOrderLoader
{
    public Task<int> CountAsync(int customerId) => db.Orders.CountAsync(o => o.CustomerId == customerId);
}

public class Strategies(IEnumerable<IHealthCheck> checks, IEnumerable<IOrderLoader> loaders, IHealthCheck check)
{
    public async Task RunEveryCheck()
    {
        foreach (var c in checks)
        {
            await c.RunAsync();
        }
    }

    public async Task EveryImplementationQueries(int customerId)
    {
        // ground-truth: loops over the registered loaders, not over data
        foreach (var loader in loaders)
        {
            await loader.CountAsync(customerId); // expect: NP0001
        }
    }

    public async Task SameServicePerElement(List<int> ids)
    {
        // ground-truth: queries only when the registered IHealthCheck is the database one
        foreach (var id in ids)
        {
            await check.RunAsync(); // expect: NP0001
        }
    }
}
