using Microsoft.EntityFrameworkCore;

namespace Corpus.Configured;

// Options from this folder's .globalconfig: methods the analyzer cannot see into are declared as
// queries, cached lookups are declared as not being queries, and a user helper iterates.
public interface ILegacyGateway
{
    Task<Order> Fetch(int id);
    void Touch(int id);
}

public interface IReportGateway
{
    Task<decimal> TotalFor(int customerId);
}

public class CachedLookup
{
    private readonly AppDb _db;
    private readonly Dictionary<int, Product> _cache = new();
    public CachedLookup(AppDb db) => _db = db;

    public Product Get(int id)
    {
        if (!_cache.TryGetValue(id, out var product))
        {
            product = _db.Products.Find(id);
            _cache[id] = product;
        }

        return product;
    }
}

public static class Batch
{
    public static void Each<T>(IReadOnlyList<T> items, Action<T> action)
    {
        for (var i = 0; i < items.Count; i++)
        {
            action(items[i]);
        }
    }
}

public class Configured
{
    private readonly AppDb _db;
    private readonly ICustomerRepository _customers;
    private readonly ILegacyGateway _legacy;
    private readonly IReportGateway _reports;
    private readonly CachedLookup _lookup;

    public Configured(AppDb db, ICustomerRepository customers, ILegacyGateway legacy, IReportGateway reports, CachedLookup lookup)
    {
        _db = db;
        _customers = customers;
        _legacy = legacy;
        _reports = reports;
        _lookup = lookup;
    }

    public async Task DeclaredQueryMethods(List<int> ids)
    {
        // ground-truth: the configuration declares these interface methods as queries; their implementations are not in the corpus
        foreach (var id in ids)
        {
            var c = await _customers.GetByIdAsync(id); // expect: NP0001
            var o = await _legacy.Fetch(id); // expect: NP0001
            _legacy.Touch(id); // expect: NP0001
        }
    }

    public async Task DataAccessTypePattern(List<int> ids)
    {
        foreach (var id in ids)
        {
            var total = await _reports.TotalFor(id); // expect: NP0006
            var n = await _customers.CountOrdersAsync(id);
        }
    }

    public void IgnoredMethod(List<int> ids)
    {
        // ground-truth: the configuration says the lookup is cached; the corpus implementation queries every time
        foreach (var id in ids)
        {
            var p = _lookup.Get(id);
        }
    }

    public void DeclaredIteratingHelper(List<int> ids)
    {
        Batch.Each(ids, id => _db.Orders.Count(o => o.CustomerId == id)); // expect: NP0001
    }
}
