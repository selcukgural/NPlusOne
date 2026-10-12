using Microsoft.EntityFrameworkCore;
using Riok.Mapperly.Abstractions;

namespace Corpus.Mapping;

// Mapperly writes the mapper methods at build time; the generated code reads what the mapping needs (CustomerName
// flattens Customer.Name), so it is followed like any other method.
[Mapper]
public partial class OrderMapperly
{
    public partial OrderView ToView(Order order);

    public partial List<OrderView> ToViews(List<Order> orders);

    [MapperIgnoreTarget(nameof(OrderSummary.CustomerName))]
    public partial OrderSummary ToSummary(Order order);
}

public class MapperlyCases(AppDb db, OrderMapperly mapper)
{
    public async Task<List<OrderView>> PerElement()
    {
        var views = new List<OrderView>();
        foreach (var order in await db.Orders.ToListAsync())
        {
            views.Add(mapper.ToView(order)); // expect: NP0002
        }

        return views;
    }

    public async Task<List<OrderView>> WholeList()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.ToViews(orders); // expect: NP0002
    }

    public async Task<List<OrderSummary>> IgnoredMember()
    {
        var summaries = new List<OrderSummary>();
        foreach (var order in await db.Orders.ToListAsync())
        {
            summaries.Add(mapper.ToSummary(order));
        }

        return summaries;
    }

    public async Task<List<OrderView>> Included()
    {
        var orders = await db.Orders.Include(o => o.Customer).ToListAsync();
        return mapper.ToViews(orders);
    }
}
