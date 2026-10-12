using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

// Queries inside delegates that run once per element.
public class Lambdas
{
    private readonly AppDb _db;
    public Lambdas(AppDb db) => _db = db;

    public List<List<Order>> SelectOverList(List<int> ids)
        => ids.Select(id => _db.Orders.Where(o => o.CustomerId == id).ToList()).ToList(); // expect: NP0001

    public Dictionary<int, int> ToDictionaryValueSelector(List<int> ids)
        => ids.ToDictionary(id => id, id => _db.Orders.Count(o => o.CustomerId == id)); // expect: NP0001

    public List<int> WhereOverArray(int[] ids)
        => ids.Where(id => _db.Orders.Any(o => o.CustomerId == id)).ToList(); // expect: NP0001

    public void ListForEach(List<int> ids)
    {
        ids.ForEach(id =>
        {
            var n = _db.Orders.Count(o => o.CustomerId == id); // expect: NP0001
        });
    }

    public void ArrayForEach(int[] ids)
    {
        Array.ForEach(ids, id => _db.Orders.Find(id)); // expect: NP0001
    }

    public async Task WhenAllOverSelect(List<int> ids)
    {
        var all = await Task.WhenAll(ids.Select(id => _db.Orders.FindAsync(id).AsTask())); // expect: NP0001
    }

    public async Task WhenAllOverAsyncLambda(List<int> ids)
    {
        var all = await Task.WhenAll(ids.Select(async id => await _db.Orders.Where(o => o.CustomerId == id).ToListAsync())); // expect: NP0001
    }

    public void ParallelForEach(List<int> ids)
    {
        // ground-truth: one DbContext used from parallel threads fails before it can be N+1; with a context per iteration it is one query per element
        Parallel.ForEach(ids, id =>
        {
            var o = _db.Orders.Find(id); // expect: NP0001
        });
    }

    public async Task ParallelForEachAsync(List<int> ids)
    {
        // ground-truth: one DbContext used from parallel threads fails before it can be N+1; with a context per iteration it is one query per element
        await Parallel.ForEachAsync(ids, async (id, token) =>
        {
            var o = await _db.Orders.FirstOrDefaultAsync(x => x.Id == id, token); // expect: NP0001
        });
    }

    public void ParallelFor(int[] ids)
    {
        // ground-truth: one DbContext used from parallel threads fails before it can be N+1; with a context per iteration it is one query per element
        Parallel.For(0, ids.Length, i =>
        {
            var o = _db.Orders.Find(ids[i]); // expect: NP0001
        });
    }

    public async Task EfForEachAsync()
    {
        await _db.Customers.ForEachAsync(c =>
        {
            var n = _db.Orders.Count(o => o.CustomerId == c.Id); // expect: NP0001
        });
    }

    public List<int> SelectManyOverList(List<int> ids)
        => ids.SelectMany(id => _db.Orders.Where(o => o.CustomerId == id).Select(o => o.Id).ToList()).ToList(); // expect: NP0001

    public async Task LocalFunctionCalledInLoop(List<int> ids)
    {
        foreach (var id in ids)
        {
            await Count(); // expect: NP0001

            async Task<int> Count() => await _db.Orders.CountAsync(o => o.CustomerId == id);
        }
    }

    public async Task LocalFunctionCalledOnce(int id)
    {
        await Count();

        async Task<int> Count() => await _db.Orders.CountAsync(o => o.CustomerId == id);
    }

    public IEnumerable<Order> Iterator(List<int> ids)
    {
        foreach (var id in ids)
        {
            yield return _db.Orders.Find(id); // expect: NP0001
        }
    }

    public List<string> LambdaInsideLoopCallingTaskRun(List<int> ids)
    {
        var names = new List<string>();
        foreach (var id in ids)
        {
            names.Add(Task.Run(() => _db.Customers.First(c => c.Id == id).Name).Result); // expect: NP0001
        }

        return names;
    }
}
