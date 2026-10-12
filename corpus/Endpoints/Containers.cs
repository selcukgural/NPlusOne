using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Endpoints;

// The serializer walks the public properties of a result container (a page, an envelope, an anonymous object)
// the same way as a bare list: entities inside it load their navigations one element at a time.

// eShop's shape: constructor parameters stored in properties of the same name.
public class PaginatedItems<TEntity>(int pageIndex, int pageSize, long count, IEnumerable<TEntity> data) where TEntity : class
{
    public int PageIndex { get; } = pageIndex;
    public int PageSize { get; } = pageSize;
    public long Count { get; } = count;
    public IEnumerable<TEntity> Data { get; } = data;
}

public record PageResult<T>(IReadOnlyList<T> Items, int Total);

public class PagedList<T>
{
    public List<T> Items { get; init; } = [];
    public int Total { get; init; }
}

// Takes the orders only to count them: nothing of them is serialized.
public class OrderSummary(IReadOnlyCollection<Order> orders)
{
    public int Count { get; } = orders.Count;
}

// Keep the orders where the serializer does not look.
public class OrderEnvelope(List<Order> orders)
{
    [JsonIgnore]
    public List<Order> Orders { get; } = orders;

    public int Count => Orders.Count;
}

public class OrderCache(List<Order> orders)
{
    internal List<Order> Orders { get; } = orders;

    public int Count => Orders.Count;
}

public record OrderRow(int Id, decimal Total);

[ApiController]
[Route("pages")]
public class PagesController(AppDb db) : ControllerBase
{
    [HttpGet("paginated")]
    public async Task<PaginatedItems<Order>> Paginated()
    {
        var orders = await db.Orders.ToListAsync();
        return new PaginatedItems<Order>(0, 10, orders.Count, orders); // expect: NP0005
    }

    [HttpGet("record")]
    public async Task<PageResult<Order>> Record()
    {
        var orders = await db.Orders.ToListAsync();
        return new PageResult<Order>(orders, orders.Count); // expect: NP0005
    }

    [HttpGet("initializer")]
    public async Task<PagedList<Customer>> Initializer()
        => new PagedList<Customer> { Items = await db.Customers.ToListAsync(), Total = 1 }; // expect: NP0005

    [HttpGet("anonymous")]
    public async Task<IActionResult> Anonymous()
    {
        var orders = await db.Orders.ToListAsync();
        return Ok(new { items = orders, total = orders.Count }); // expect: NP0005
    }

    [HttpGet("through-local")]
    public async Task<IActionResult> ThroughLocal()
    {
        var orders = await db.Orders.ToListAsync();
        var page = new PageResult<Order>(orders, orders.Count);
        return Ok(page); // expect: NP0005
    }

    [HttpGet("nested")]
    public async Task<IActionResult> Nested()
    {
        var orders = await db.Orders.ToListAsync();
        return Ok(new { page = new PageResult<Order>(orders, orders.Count) }); // expect: NP0005
    }

    // One order: its lines are one query, each line's product one more.
    [HttpGet("single")]
    public async Task<IActionResult> Single() => Ok(new { order = await db.Orders.FirstAsync() }); // expect: NP0005

    [HttpGet("rows")]
    public async Task<PageResult<OrderRow>> Rows()
    {
        var rows = await db.Orders.Select(o => new OrderRow(o.Id, o.Total)).ToListAsync();
        return new PageResult<OrderRow>(rows, rows.Count);
    }

    [HttpGet("summary")]
    public async Task<OrderSummary> Summary() => new OrderSummary(await db.Orders.ToListAsync());

    [HttpGet("envelope")]
    public async Task<OrderEnvelope> Envelope() => new OrderEnvelope(await db.Orders.ToListAsync());

    [HttpGet("cache")]
    public async Task<OrderCache> Cache() => new OrderCache(await db.Orders.ToListAsync());

    [HttpGet("anonymous-count")]
    public async Task<IActionResult> AnonymousCount()
    {
        var orders = await db.Orders.ToListAsync();
        return Ok(new { total = orders.Count });
    }

    [HttpGet("anonymous-included")]
    public async Task<IActionResult> AnonymousIncluded()
        => Ok(new { items = await db.Customers.Include(c => c.Address).Include(c => c.Orders).ThenInclude(o => o.Lines).ThenInclude(l => l.Product).ToListAsync() });

    // A single customer with everything below it included.
    [HttpGet("single-included")]
    public async Task<IActionResult> SingleIncluded()
        => Ok(new { customer = await db.Customers.Include(c => c.Orders).ThenInclude(o => o.Lines).ThenInclude(l => l.Product).FirstAsync() });
}

public static class ContainerEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/pages/typed", async (AppDb db) =>
        {
            var orders = await db.Orders.ToListAsync();
            return TypedResults.Ok(new PaginatedItems<Order>(0, 10, orders.Count, orders)); // expect: NP0005
        });
        app.MapGet("/pages/anonymous", async (AppDb db) => new { customers = await db.Customers.ToListAsync() }); // expect: NP0005
        app.MapGet("/pages/rows", async (AppDb db) => new { rows = await db.Orders.Select(o => new OrderRow(o.Id, o.Total)).ToListAsync() });
    }
}

public class ContainerSerialization(AppDb db)
{
    private static readonly JsonSerializerOptions Json = new() { ReferenceHandler = ReferenceHandler.IgnoreCycles };

    public async Task<string> Anonymous()
    {
        var orders = await db.Orders.ToListAsync();
        return JsonSerializer.Serialize(new { orders }, Json); // expect: NP0005
    }

    public async Task<string> Summary() => JsonSerializer.Serialize(new OrderSummary(await db.Orders.ToListAsync()), Json);
}
