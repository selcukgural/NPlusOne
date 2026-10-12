using AutoMapper;
using AutoMapper.QueryableExtensions;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Mapping;

public class OrderView
{
    public int Id { get; set; }
    public string CustomerName { get; set; }
}

public class OrderSummary
{
    public int Id { get; set; }
    public string CustomerName { get; set; }
}

public class OrderWithCity
{
    public int Id { get; set; }
    public string City { get; set; }
}

public class CustomerView
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public class LineView
{
    public int Id { get; set; }
    public string ProductName { get; set; }
}

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        CreateMap<Order, OrderView>();
        CreateMap<Order, OrderSummary>().ForMember(d => d.CustomerName, o => o.Ignore());
        CreateMap<Order, OrderWithCity>().ForMember(d => d.City, o => o.MapFrom(s => s.Customer.Address.City));
        CreateMap<Customer, CustomerView>();
    }
}

public class MappingCases(AppDb db, IMapper mapper)
{
    public async Task<List<OrderView>> ConventionFlattening()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderView>>(orders); // expect: NP0002
    }

    public async Task<List<OrderSummary>> IgnoredMember()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderSummary>>(orders);
    }

    public async Task<List<OrderWithCity>> ExplicitMapFrom()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderWithCity>>(orders); // expect: NP0002
    }

    public async Task<List<OrderView>> Included()
    {
        var orders = await db.Orders.Include(o => o.Customer).ToListAsync();
        return mapper.Map<List<OrderView>>(orders);
    }

    public async Task<List<OrderView>> Untracked()
    {
        var orders = await db.Orders.AsNoTracking().ToListAsync();
        return mapper.Map<List<OrderView>>(orders); // expect: NP0002
    }

    public async Task<List<CustomerView>> ScalarsOnly()
    {
        var customers = await db.Customers.ToListAsync();
        return mapper.Map<List<CustomerView>>(customers);
    }

    public async Task<List<OrderView>> OneByOneInALoop()
    {
        var orders = await db.Orders.ToListAsync();
        var views = new List<OrderView>();
        foreach (var o in orders)
        {
            views.Add(mapper.Map<OrderView>(o)); // expect: NP0002
        }

        return views;
    }

    public async Task<OrderView> SingleEntity(int id)
    {
        var order = await db.Orders.FirstAsync(o => o.Id == id);
        return mapper.Map<OrderView>(order);
    }

    public List<OrderView> ProjectedInSql() => db.Orders.ProjectTo<OrderView>(mapper.ConfigurationProvider).ToList();

    public async Task<List<LineView>> NoVisibleProfile()
    {
        // ground-truth: AutoMapper needs the map, which in this case lives in a project the corpus does not have
        var lines = await db.OrderLines.ToListAsync();
        return mapper.Map<List<LineView>>(lines); // expect: NP0007
    }

    public async Task<List<OrderView>> Mapster()
    {
        var orders = await db.Orders.ToListAsync();
        return orders.Adapt<List<OrderView>>(); // expect: NP0007
    }
}
