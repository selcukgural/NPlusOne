using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

// Reading a property or an indexer runs its getter: one that queries does so once per element in a loop. A delegate
// kept in a field, or a compiled query, runs its query on every call.
public class CustomerRow(AppDb db)
{
    public int CustomerId { get; set; }

    public string Label { get; set; } = "";

    public int OrderCount => db.Orders.Count(o => o.CustomerId == CustomerId);

    public string Display => "#" + CustomerId;

    // Assigning runs the setter only.
    public string Note
    {
        get => db.Customers.First(c => c.Id == CustomerId).Name;
        set => Label = value;
    }
}

public class CustomerLookup(AppDb db)
{
    public Customer? this[int id] => db.Customers.FirstOrDefault(c => c.Id == id);
}

public class NameLookup
{
    private readonly Dictionary<int, string> _names = new() { [1] = "one" };

    public string? this[int id] => _names.GetValueOrDefault(id);
}

public class Accessors(AppDb db, CustomerLookup lookup, NameLookup names)
{
    public async Task<int> GetterPerElement()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            var row = new CustomerRow(db) { CustomerId = customer.Id };
            total += row.OrderCount; // expect: NP0001
        }

        return total;
    }

    public async Task<int> GetterThroughHelperPerElement()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += CountOf(new CustomerRow(db) { CustomerId = customer.Id }); // expect: NP0001
        }

        return total;
    }

    private static int CountOf(CustomerRow row) => row.OrderCount;

    public async Task<int> PlainPropertiesPerElement()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            var row = new CustomerRow(db) { CustomerId = customer.Id, Label = customer.Name };
            row.Note = customer.Name;
            total += row.Label.Length + row.Display.Length;
        }

        return total;
    }

    public int GetterOnce() => new CustomerRow(db) { CustomerId = 1 }.OrderCount;

    public async Task<int> IndexerPerElement()
    {
        var found = 0;
        foreach (var id in await db.Customers.Select(c => c.Id).ToListAsync())
        {
            if (lookup[id] is not null) // expect: NP0001
            {
                found++;
            }
        }

        return found;
    }

    public async Task<int> InMemoryIndexerPerElement()
    {
        var found = 0;
        foreach (var id in await db.Customers.Select(c => c.Id).ToListAsync())
        {
            if (names[id] is not null)
            {
                found++;
            }
        }

        return found;
    }
}

public class DelegateFields(AppDb db)
{
    private readonly Func<int, int> _count = id => db.Orders.Count(o => o.CustomerId == id);
    private readonly Func<int, int> _twice = id => id * 2;

    private static readonly Func<AppDb, int, int> CountFor = EF.CompileQuery((AppDb context, int id) => context.Orders.Count(o => o.CustomerId == id));
    private readonly Func<AppDb, int, int> _countFor = EF.CompileQuery((AppDb context, int id) => context.Orders.Count(o => o.CustomerId == id));
    private readonly Func<AppDb, int, Task<Order?>> _firstFor = EF.CompileAsyncQuery((AppDb context, int id) => context.Orders.FirstOrDefault(o => o.CustomerId == id));

    public async Task<int> FieldPerElement()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += _count(customer.Id); // expect: NP0001
        }

        return total;
    }

    public async Task<int> PureFieldPerElement()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += _twice(customer.Id);
        }

        return total;
    }

    public async Task<int> CompiledPerElement()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            // ground-truth: a static compiled query stays bound to the first model, and the harness builds one per run; the instance fields below run
            total += CountFor(db, customer.Id); // expect: NP0001
        }

        return total;
    }

    public async Task<int> CompiledAsyncPerElement()
    {
        var found = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            if (await _firstFor(db, customer.Id) is not null) // expect: NP0001
            {
                found++;
            }
        }

        return found;
    }

    public async Task<int> CompiledThroughHelperPerElement()
    {
        var total = 0;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            total += Count(customer.Id); // expect: NP0001
        }

        return total;
    }

    private int Count(int id) => _countFor(db, id);

    public int CompiledOnce() => CountFor(db, 1);
}
