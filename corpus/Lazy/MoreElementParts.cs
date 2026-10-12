using Microsoft.EntityFrameworkCore;

namespace Corpus.Lazy;

// Parts of elements built per element in shapes beyond tuples in foreach: a part read in a lambda, Zip, key/value pairs
// and records built in Select, and an index stepped in a while loop.
public record OrderRow(int Id, Order Order);

public record CustomerRow(int Id, Customer Customer);

public class MoreElementParts(AppDb db)
{
    public async Task<int> PartInLambda()
    {
        var pairs = (await db.Orders.ToListAsync()).Select(o => (o.Id, Order: o)).ToList();
        return pairs.Sum(p => p.Order.Customer.Name.Length); // expect: NP0002
    }

    public async Task<int> SharedPartInLambda()
    {
        var orders = await db.Orders.ToListAsync();
        var first = orders[0].Customer;
        var pairs = orders.Select(o => (o.Id, Customer: first)).ToList();
        return pairs.Sum(p => p.Customer.Address == null ? 0 : 1);
    }

    public async Task<int> Zipped()
    {
        var orders = await db.Orders.ToListAsync();
        var total = 0;
        foreach (var (order, position) in orders.Zip(Enumerable.Range(0, orders.Count)))
        {
            total += order.Customer.Name.Length + position; // expect: NP0002
        }

        return total;
    }

    public async Task<int> ZippedPairs()
    {
        var orders = await db.Orders.ToListAsync();
        var total = 0;
        foreach (var pair in orders.Zip(Enumerable.Range(0, orders.Count)))
        {
            total += pair.First.Customer.Name.Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> KeyValuePairs()
    {
        var total = 0;
        foreach (var pair in (await db.Orders.ToListAsync()).Select(o => new KeyValuePair<int, Order>(o.Id, o)))
        {
            total += pair.Value.Customer.Name.Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> RecordsDeconstructed()
    {
        var total = 0;
        foreach (var (id, order) in (await db.Orders.ToListAsync()).Select(o => new OrderRow(o.Id, o)))
        {
            total += id + order.Customer.Name.Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> RecordProperty()
    {
        var total = 0;
        foreach (var row in (await db.Orders.ToListAsync()).Select(o => new OrderRow(o.Id, o)))
        {
            total += row.Order.Customer.Name.Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> SharedRecordProperty()
    {
        var orders = await db.Orders.ToListAsync();
        var first = orders[0].Customer;
        var total = 0;
        foreach (var row in orders.Select(o => new CustomerRow(o.Id, first)))
        {
            total += row.Customer.Address == null ? 0 : 1;
        }

        return total;
    }

    public async Task<int> IndexInWhile()
    {
        var orders = await db.Orders.ToListAsync();
        var total = 0;
        var i = 0;
        while (i < orders.Count)
        {
            total += orders[i].Customer.Name.Length; // expect: NP0002
            i++;
        }

        return total;
    }

    public async Task<int> FixedIndexInWhile()
    {
        var orders = await db.Orders.ToListAsync();
        var total = 0;
        var i = 0;
        while (i < orders.Count)
        {
            total += orders[0].Customer.Name.Length;
            i++;
        }

        return total;
    }
}
