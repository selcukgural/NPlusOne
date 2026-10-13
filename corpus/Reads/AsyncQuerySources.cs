using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

// A repository from a package hands out its EF query behind a task: ABP's GetQueryableAsync() and GetDbSetAsync(), or a
// base repository class of the application's own framework. The bodies are not in the compilation; the awaited query
// runs once per element like any other.
public interface IOrderQuerySource
{
    Task<IQueryable<Order>> GetQueryableAsync();

    ValueTask<IQueryable<Order>> GetQueryableValueAsync();

    Task<DbSet<Order>> GetDbSetAsync();

    Task<IQueryable<OrderSummaryRow>> GetSummaryRowsAsync();
}

public class OrderSummaryRow
{
    public int CustomerId { get; set; }
    public decimal Total { get; set; }
}

public class AsyncQuerySources(IOrderQuerySource source)
{
    public async Task<int> AwaitedQueryableRunPerElement(List<int> customerIds)
    {
        var count = 0;
        foreach (var id in customerIds)
        {
            // ground-truth: the interface has no implementation in the corpus; ABP's EfCoreRepository behind it is proven in corpus/Abp
            var orders = await source.GetQueryableAsync();
            count += await orders.CountAsync(o => o.CustomerId == id); // expect: NP0001
        }

        return count;
    }

    public async Task<int> AwaitedValueTaskQueryableRunPerElement(List<int> customerIds)
    {
        var count = 0;
        foreach (var id in customerIds)
        {
            // ground-truth: the interface has no implementation in the corpus; ABP's EfCoreRepository behind it is proven in corpus/Abp
            var orders = await source.GetQueryableValueAsync();
            count += orders.Count(o => o.CustomerId == id); // expect: NP0001
        }

        return count;
    }

    public async Task<int> AwaitedDbSetRunPerElement(List<int> customerIds)
    {
        var count = 0;
        var orders = await source.GetDbSetAsync();
        foreach (var id in customerIds)
        {
            // ground-truth: the interface has no implementation in the corpus; ABP's EfCoreRepository behind it is proven in corpus/Abp
            var first = await orders.FirstOrDefaultAsync(o => o.CustomerId == id); // expect: NP0001
            count += first?.Id ?? 0;
        }

        return count;
    }

    public async Task<int> AwaitedQueryableRunOnce(List<int> customerIds)
    {
        var count = 0;
        // ground-truth: the interface has no implementation in the corpus
        var orders = await (await source.GetQueryableAsync()).ToListAsync();
        foreach (var id in customerIds)
        {
            count += orders.Count(o => o.CustomerId == id);
        }

        return count;
    }

    public async Task<int> AwaitedQueryableOfNonEntities(List<int> customerIds)
    {
        var count = 0;
        foreach (var id in customerIds)
        {
            // ground-truth: the interface has no implementation in the corpus
            var rows = await source.GetSummaryRowsAsync();
            count += rows.Count(r => r.CustomerId == id);
        }

        return count;
    }
}
