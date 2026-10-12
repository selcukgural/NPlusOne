using AutoMapper;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Mapping;

// The other overloads and configuration styles of the real AutoMapper and Mapster APIs.
public class OrderBrief
{
    public int Id { get; set; }
    public string CustomerName { get; set; }
}

public class OrderRow
{
    public int Id { get; set; }
    public string CustomerName { get; set; }
}

public class OrderCard
{
    public int Id { get; set; }
    public string CustomerName { get; set; }
}

public class OrderTile
{
    public int Id { get; set; }
    public string Town { get; set; }
}

public class OrderWithCustomer
{
    public int Id { get; set; }
    public CustomerView Customer { get; set; }
}

public class ReverseProfile : Profile
{
    public ReverseProfile()
    {
        // ForMember after another chained call still configures the same map.
        CreateMap<Order, OrderRow>()
            .AfterMap((source, destination) => destination.Id = source.Id)
            .ForMember(d => d.CustomerName, o => o.Ignore())
            .ReverseMap();

        // ForMember after ReverseMap configures the opposite map (OrderWithCustomer → Order) only.
        CreateMap<Order, OrderWithCustomer>().ReverseMap().ForMember(d => d.Customer, o => o.Ignore());
    }
}

public static class MapsterSetup
{
    public static void Configure()
    {
        TypeAdapterConfig<Order, OrderCard>.NewConfig().Ignore(d => d.CustomerName);
        TypeAdapterConfig<Order, OrderTile>.NewConfig().Map(d => d.Town, s => s.Customer.Address.City);
    }
}

public class RealApis(AppDb db, AutoMapper.IMapper mapper, MapsterMapper.IMapper mapsterMapper)
{
    public static AutoMapper.IMapper Inline()
        => new MapperConfiguration(cfg => cfg.CreateMap<Order, OrderBrief>().ForMember(d => d.CustomerName, o => o.Ignore())).CreateMapper();

    public async Task<List<OrderView>> SourceAndDestinationTypes()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<Order>, List<OrderView>>(orders); // expect: NP0002
    }

    public async Task<List<OrderView>> IntoExistingList()
    {
        var orders = await db.Orders.ToListAsync();
        var views = new List<OrderView>();
        return mapper.Map(orders, views); // expect: NP0002
    }

    public async Task<List<OrderBrief>> InlineConfigurationIgnores()
    {
        var orders = await db.Orders.ToListAsync();
        return Inline().Map<List<OrderBrief>>(orders);
    }

    public async Task<List<OrderRow>> IgnoredAfterAnotherCall()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderRow>>(orders);
    }

    public async Task<List<OrderWithCustomer>> IgnoredOnTheReverseMapOnly()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderWithCustomer>>(orders); // expect: NP0002
    }

    public Task<List<OrderView>> ProjectedThroughTheMapper() => mapper.ProjectTo<OrderView>(db.Orders).ToListAsync();

    public Task<List<OrderView>> ProjectedByMapster() => db.Orders.ProjectToType<OrderView>().ToListAsync();

    public async Task<List<OrderView>> MapsterMapperInterface()
    {
        var orders = await db.Orders.ToListAsync();
        return mapsterMapper.Map<List<OrderView>>(orders); // expect: NP0007
    }

    public async Task<List<OrderCard>> MapsterConfigurationIgnores()
    {
        var orders = await db.Orders.ToListAsync();
        return orders.Adapt<List<OrderCard>>();
    }

    public async Task<List<OrderTile>> MapsterConfigurationMaps()
    {
        var orders = await db.Orders.ToListAsync();
        return orders.Adapt<List<OrderTile>>(); // expect: NP0002
    }

    public async Task<List<OrderTile>> MapsterConfigurationMapsIncluded()
    {
        var orders = await db.Orders.Include(o => o.Customer).ThenInclude(c => c.Address).ToListAsync();
        return orders.Adapt<List<OrderTile>>();
    }
}
