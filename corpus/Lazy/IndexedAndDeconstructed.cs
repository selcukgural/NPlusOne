using Microsoft.EntityFrameworkCore;

namespace Corpus.Lazy;

// Elements reached without a foreach variable or a lambda parameter: by index in a for loop, or as a part of a
// tuple or key/value pair the loop deconstructs. Each is still one element per iteration.
public class IndexedAndDeconstructed
{
    private readonly AppDb _db;

    public IndexedAndDeconstructed(AppDb db) => _db = db;

    public async Task<int> ListByIndex()
    {
        var orders = await _db.Orders.ToListAsync();
        var length = 0;
        for (var i = 0; i < orders.Count; i++)
        {
            length += orders[i].Customer.Name.Length; // expect: NP0002
        }

        return length;
    }

    public async Task<int> ArrayByIndexBackwards()
    {
        var orders = await _db.Orders.ToArrayAsync();
        var length = 0;
        for (var i = orders.Length - 1; i >= 0; i--)
        {
            var order = orders[i];
            length += order.Customer.Name.Length; // expect: NP0002
        }

        return length;
    }

    public async Task<int> IncludedByIndex()
    {
        var orders = await _db.Orders.Include(o => o.Customer).ToListAsync();
        var length = 0;
        for (var i = 0; i < orders.Count; i++)
        {
            length += orders[i].Customer.Name.Length;
        }

        return length;
    }

    // The same element on every iteration: its customer loads once.
    public async Task<int> SameElementInEveryIteration()
    {
        var orders = await _db.Orders.ToListAsync();
        var length = 0;
        for (var i = 0; i < orders.Count; i++)
        {
            length += orders[0].Customer.Name.Length;
        }

        return length;
    }

    // The loop declares the index but never steps it: the same element on every iteration.
    public async Task<int> IndexDeclaredByTheLoopButNotStepped()
    {
        var orders = await _db.Orders.ToListAsync();
        var length = 0;
        for (int i = 0, last = orders.Count - 1; i < orders.Count; i++)
        {
            length += orders[last].Customer.Name.Length;
        }

        return length;
    }

    // Outside a loop, one element is one query.
    public async Task<string> FirstByIndex()
    {
        var orders = await _db.Orders.ToListAsync();
        return orders[0].Customer.Name;
    }

    public async Task<int> DeconstructedTuples()
    {
        var orders = await _db.Orders.ToListAsync();
        var length = 0;
        foreach (var (id, order) in orders.Select(o => (o.Id, o)))
        {
            length += id + order.Customer.Name.Length; // expect: NP0002
        }

        return length;
    }

    public async Task<int> DeconstructedTuplesFromALocal()
    {
        var orders = await _db.Orders.ToListAsync();
        var pairs = orders.Select(o => (Order: o, Total: o.Total)).ToList();
        var length = 0;
        foreach (var (order, total) in pairs)
        {
            length += (int)total + order.Customer.Name.Length; // expect: NP0002
        }

        return length;
    }

    public async Task<int> TupleItemsWithoutDeconstruction()
    {
        var orders = await _db.Orders.ToListAsync();
        var length = 0;
        foreach (var pair in orders.Select(o => (o.Id, Order: o)))
        {
            length += pair.Order.Customer.Name.Length; // expect: NP0002
        }

        return length;
    }

    public async Task<int> DeconstructedDictionary()
    {
        var byId = await _db.Orders.ToDictionaryAsync(o => o.Id);
        var length = 0;
        foreach (var (id, order) in byId)
        {
            length += id + order.Customer.Name.Length; // expect: NP0002
        }

        return length;
    }

    public async Task<int> DictionaryValues()
    {
        var byId = (await _db.Orders.ToListAsync()).ToDictionary(o => o.Id);
        var length = 0;
        foreach (var pair in byId)
        {
            length += pair.Value.Customer.Name.Length; // expect: NP0002
        }

        return length;
    }

    public async Task<int> DeconstructedIncluded()
    {
        var byId = await _db.Orders.Include(o => o.Customer).ToDictionaryAsync(o => o.Id);
        var length = 0;
        foreach (var (_, order) in byId)
        {
            length += order.Customer.Name.Length;
        }

        return length;
    }

    // The customer is the same entity in every tuple: its address loads once.
    public async Task<int> DeconstructedSharedEntity()
    {
        var customer = await _db.Customers.FirstAsync();
        var orders = await _db.Orders.ToListAsync();
        var length = 0;
        foreach (var (order, owner) in orders.Select(o => (o, customer)))
        {
            length += order.Id + owner.Address.City.Length;
        }

        return length;
    }

    public async Task<int> DictionaryOfTuples()
    {
        var orders = await _db.Orders.ToListAsync();
        var length = 0;
        foreach (var (id, order) in orders.Select(o => (o.Id, o)).ToDictionary())
        {
            length += id + order.Customer.Name.Length; // expect: NP0002
        }

        return length;
    }

    // ToDictionary() over tuples: the values are the shared customer, not the orders.
    public async Task<int> DictionaryOfTuplesWithASharedEntity()
    {
        var customer = await _db.Customers.FirstAsync();
        var orders = await _db.Orders.ToListAsync();
        var length = 0;
        foreach (var (id, owner) in orders.Select(o => (o.Id, customer)).ToDictionary())
        {
            length += id + owner.Address.City.Length;
        }

        return length;
    }

    // Every tuple holds the same customer: its address loads once.
    public async Task<int> TupleItemWithASharedEntity()
    {
        var customer = await _db.Customers.FirstAsync();
        var orders = await _db.Orders.ToListAsync();
        var length = 0;
        foreach (var pair in orders.Select(o => (Order: o, Owner: customer)))
        {
            length += pair.Order.Id + pair.Owner.Address.City.Length;
        }

        return length;
    }

    // Projected values, no entities: nothing loads.
    public async Task<int> DeconstructedProjection()
    {
        var rows = await _db.Orders.Select(o => new { o.Id, o.Customer.Name }).ToListAsync();
        var length = 0;
        foreach (var (id, name) in rows.Select(r => (r.Id, r.Name)))
        {
            length += id + name.Length;
        }

        return length;
    }

#if NET9_0_OR_GREATER
    public async Task<int> DeconstructedIndex()
    {
        var orders = await _db.Orders.ToListAsync();
        var length = 0;
        foreach (var (index, order) in orders.Index())
        {
            length += index + order.Customer.Name.Length; // expect: NP0002
        }

        return length;
    }
#endif
}
