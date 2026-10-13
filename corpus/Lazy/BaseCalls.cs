using Microsoft.EntityFrameworkCore;

namespace Corpus.Lazy;

// A base call runs the base body only: it reads what the base reads and returns what the base returns, never what an
// override does. A dispatched call in the same class can run the override.
public class PlainLabels
{
    public virtual string Label(Order order) => order.Total.ToString("0.00");
}

public class CustomerLabels(AppDb db) : PlainLabels
{
    public override string Label(Order order) => order.Customer.Name;

    public async Task<int> BaseInLoop()
    {
        var total = 0;
        foreach (var order in await db.Orders.ToListAsync())
        {
            total += base.Label(order).Length;
        }

        return total;
    }

    public async Task<int> DispatchedInLoop()
    {
        var total = 0;
        foreach (var order in await db.Orders.ToListAsync())
        {
            total += Label(order).Length; // expect: NP0002
        }

        return total;
    }
}

public class OrderSource(AppDb db)
{
    public virtual IQueryable<Order> Orders() => db.Orders;
}

// Only the override includes the customer: the orders base.Orders() returns load it once per order.
public class OrderSourceWithCustomers(AppDb db) : OrderSource(db)
{
    public override IQueryable<Order> Orders() => db.Orders.Include(o => o.Customer);

    public async Task<int> BaseQuery()
    {
        var total = 0;
        foreach (var order in await base.Orders().ToListAsync())
        {
            total += order.Customer.Name.Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> OwnQuery()
    {
        var total = 0;
        foreach (var order in await Orders().ToListAsync())
        {
            total += order.Customer.Name.Length;
        }

        return total;
    }
}
