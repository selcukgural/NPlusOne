using AutoMapper;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Mapping;

// AutoMapper configuration that reads source members outside the naming conventions: IncludeMembers, ForPath,
// value resolvers and type converters, and Map passed as a method group.
public class OrderNameView
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public class OrderPlaceView
{
    public int Id { get; set; }
    public string AddressCity { get; set; }
}

public class BuyerView
{
    public string Name { get; set; }
}

public class OrderBuyerView
{
    public int Id { get; set; }
    public BuyerView Buyer { get; set; }
}

public class OrderCustomerPath
{
    public int Id { get; set; }
    public CustomerView Customer { get; set; }
}

public class OrderBuyerName
{
    public int Id { get; set; }
    public string Buyer { get; set; }
}

public class OrderTown
{
    public int Id { get; set; }
    public string Town { get; set; }
}

public class OrderLineCount
{
    public int Id { get; set; }
    public int Lines { get; set; }
}

public class OrderLabel
{
    public int Id { get; set; }
    public string CustomerName { get; set; }
}

public class OrderConverted
{
    public int Id { get; set; }
    public string Buyer { get; set; }
}

public class OrderConvertedByLambda
{
    public int Id { get; set; }
    public string CustomerName { get; set; }
}

public class OrderConvertedWithCustomer
{
    public int Id { get; set; }
    public string Buyer { get; set; }
}

public class OrderConditional
{
    public int Id { get; set; }
    public string CustomerName { get; set; }
}

public class BuyerNameResolver : IValueResolver<Order, OrderBuyerName, string>
{
    public string Resolve(Order source, OrderBuyerName destination, string destMember, ResolutionContext context) => source.Customer.Name;
}

public class CityResolver : IMemberValueResolver<Order, OrderTown, Customer, string>
{
    public string Resolve(Order source, OrderTown destination, Customer sourceMember, string destMember, ResolutionContext context)
        => sourceMember.Address?.City;
}

public class LineCountResolver(AppDb db) : IValueResolver<Order, OrderLineCount, int>
{
    public int Resolve(Order source, OrderLineCount destination, int destMember, ResolutionContext context)
        => db.OrderLines.Count(l => l.OrderId == source.Id);
}

// Replaces the convention for CustomerName without reading the customer.
public class FixedLabelResolver : IValueResolver<Order, OrderLabel, string>
{
    public string Resolve(Order source, OrderLabel destination, string destMember, ResolutionContext context) => "order " + source.Id;
}

public class OrderConverter : ITypeConverter<Order, OrderConverted>
{
    public OrderConverted Convert(Order source, OrderConverted destination, ResolutionContext context)
        => new() { Id = source.Id, Buyer = source.Customer.Name };
}

public class AutoMapperMembersProfile : Profile
{
    public AutoMapperMembersProfile()
    {
        CreateMap<Order, OrderNameView>().IncludeMembers(s => s.Customer);
        CreateMap<Customer, OrderNameView>(MemberList.None);
        CreateMap<Order, OrderPlaceView>().IncludeMembers(s => s.Customer);
        CreateMap<Customer, OrderPlaceView>(MemberList.None);
        CreateMap<Order, OrderBuyerView>().ForPath(d => d.Buyer.Name, o => o.MapFrom(s => s.Customer.Name));
        CreateMap<Order, OrderCustomerPath>().ForPath(d => d.Customer.Name, o => o.Ignore());
        CreateMap<Order, OrderBuyerName>().ForMember(d => d.Buyer, o => o.MapFrom<BuyerNameResolver>());
        CreateMap<Order, OrderTown>().ForMember(d => d.Town, o => o.MapFrom<CityResolver, Customer>(s => s.Customer));
        CreateMap<Order, OrderLineCount>().ForMember(d => d.Lines, o => o.MapFrom<LineCountResolver>());
        CreateMap<Order, OrderLabel>().ForMember(d => d.CustomerName, o => o.MapFrom<FixedLabelResolver>());
        CreateMap<Order, OrderConverted>().ConvertUsing<OrderConverter>();
        // A condition alone leaves the member to the conventions.
        CreateMap<Order, OrderConditional>().ForMember(d => d.CustomerName, o => o.Condition(s => s.Id > 0));
        CreateMap<Order, OrderConvertedByLambda>().ConvertUsing(s => new OrderConvertedByLambda { Id = s.Id });
        CreateMap<Order, OrderConvertedWithCustomer>().ConvertUsing((s, _) => new OrderConvertedWithCustomer { Id = s.Id, Buyer = s.Customer.Name });
    }
}

public class AutoMapperMembers(AppDb db, IMapper mapper)
{
    public async Task<List<OrderNameView>> IncludedMember()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderNameView>>(orders); // expect: NP0002
    }

    public async Task<List<OrderNameView>> IncludedMemberLoaded()
    {
        var orders = await db.Orders.Include(o => o.Customer).ToListAsync();
        return mapper.Map<List<OrderNameView>>(orders);
    }

    public async Task<List<OrderPlaceView>> IncludedMemberFlattened()
    {
        var orders = await db.Orders.Include(o => o.Customer).ToListAsync();
        return mapper.Map<List<OrderPlaceView>>(orders); // expect: NP0002
    }

    public async Task<List<OrderPlaceView>> IncludedMemberFlattenedLoaded()
    {
        var orders = await db.Orders.Include(o => o.Customer).ThenInclude(c => c.Address).ToListAsync();
        return mapper.Map<List<OrderPlaceView>>(orders);
    }

    public async Task<List<OrderBuyerView>> ForPathMapFrom()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderBuyerView>>(orders); // expect: NP0002
    }

    public async Task<List<OrderBuyerView>> ForPathMapFromLoaded()
    {
        var orders = await db.Orders.Include(o => o.Customer).ToListAsync();
        return mapper.Map<List<OrderBuyerView>>(orders);
    }

    public async Task<List<OrderCustomerPath>> ForPathIgnored()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderCustomerPath>>(orders);
    }

    public async Task<List<OrderBuyerName>> ValueResolver()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderBuyerName>>(orders); // expect: NP0002
    }

    public async Task<List<OrderBuyerName>> ValueResolverLoaded()
    {
        var orders = await db.Orders.Include(o => o.Customer).ToListAsync();
        return mapper.Map<List<OrderBuyerName>>(orders);
    }

    public async Task<List<OrderTown>> MemberValueResolver()
    {
        var orders = await db.Orders.Include(o => o.Customer).ToListAsync();
        return mapper.Map<List<OrderTown>>(orders); // expect: NP0002
    }

    public async Task<List<OrderTown>> MemberValueResolverLoaded()
    {
        var orders = await db.Orders.Include(o => o.Customer).ThenInclude(c => c.Address).ToListAsync();
        return mapper.Map<List<OrderTown>>(orders);
    }

    public async Task<List<OrderLineCount>> QueryingResolver()
    {
        var orders = await db.Orders.Include(o => o.Lines).ToListAsync();
        return mapper.Map<List<OrderLineCount>>(orders); // expect: NP0001
    }

    public async Task<List<OrderLabel>> ResolverReplacesConvention()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderLabel>>(orders);
    }

    public async Task<List<OrderConverted>> TypeConverter()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderConverted>>(orders); // expect: NP0002
    }

    public async Task<List<OrderConverted>> TypeConverterLoaded()
    {
        var orders = await db.Orders.Include(o => o.Customer).ToListAsync();
        return mapper.Map<List<OrderConverted>>(orders);
    }

    public async Task<List<OrderConvertedByLambda>> ConverterLambdaReplacesConvention()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderConvertedByLambda>>(orders);
    }

    public async Task<List<OrderConvertedWithCustomer>> ConverterLambda()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderConvertedWithCustomer>>(orders); // expect: NP0002
    }

    public async Task<List<OrderConditional>> ConditionOnly()
    {
        var orders = await db.Orders.ToListAsync();
        return mapper.Map<List<OrderConditional>>(orders); // expect: NP0002
    }

    public async Task<List<OrderView>> MethodGroup()
    {
        var orders = await db.Orders.ToListAsync();
        return orders.Select(mapper.Map<OrderView>).ToList(); // expect: NP0002
    }

    public async Task<List<OrderSummary>> MethodGroupIgnored()
    {
        var orders = await db.Orders.ToListAsync();
        return orders.Select(mapper.Map<OrderSummary>).ToList();
    }

    public async Task<List<OrderView>> MethodGroupLoaded()
    {
        var orders = await db.Orders.Include(o => o.Customer).ToListAsync();
        return orders.Select(mapper.Map<OrderView>).ToList();
    }
}
