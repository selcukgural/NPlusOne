using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Endpoints;

// The serializer reads every property of what an endpoint returns. With lazy loading on, each
// navigation it reads on each element is one more query.
[ApiController]
[Route("orders")]
public class OrdersController : ControllerBase
{
    private readonly AppDb _db;
    public OrdersController(AppDb db) => _db = db;

    [HttpGet]
    public async Task<List<Order>> All() => await _db.Orders.ToListAsync(); // expect: NP0005

    [HttpGet("action-result")]
    public async Task<ActionResult<IEnumerable<Order>>> AllAsActionResult()
    {
        var orders = await _db.Orders.Where(o => o.Total > 0).ToListAsync();
        return Ok(orders); // expect: NP0005
    }

    [HttpGet("included")]
    public async Task<List<Order>> AllIncluded()
        => await _db.Orders.Include(o => o.Customer).Include(o => o.Lines).ToListAsync(); // expect: NP0005

    [HttpGet("addresses")]
    public async Task<List<Address>> EntitiesWithoutNavigations() => await _db.Set<Address>().ToListAsync();

    [HttpGet("fully-included")]
    public async Task<List<OrderLine>> FullyIncluded() => await _db.OrderLines.Include(l => l.Product).ToListAsync();

    [HttpGet("dto")]
    public async Task<List<OrderDto>> AllAsDto()
        => await _db.Orders.Select(o => new OrderDto(o.Id, o.Customer.Name)).ToListAsync();

    [HttpGet("{id}")]
    public async Task<Order> One(int id) => await _db.Orders.FirstAsync(o => o.Id == id); // expect: NP0005

    [HttpGet("untracked")]
    public async Task<List<Order>> Untracked() => await _db.Orders.AsNoTracking().ToListAsync(); // expect: NP0005

    [NonAction]
    public Task<List<Order>> NotAnEndpoint() => _db.Orders.ToListAsync();
}

public record OrderDto(int Id, string CustomerName);

public static class MinimalApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/customers", async (AppDb db) => await db.Customers.ToListAsync()); // expect: NP0005
        app.MapGet("/customers/typed", async (AppDb db) => TypedResults.Ok(await db.Customers.ToListAsync())); // expect: NP0005
        app.MapGet("/customers/names", async (AppDb db) => await db.Customers.Select(c => c.Name).ToListAsync());

        // Results.Json and TypedResults.Json take the value as "data", followed by optional arguments.
        app.MapGet("/customers/json", async (AppDb db) => Results.Json(await db.Customers.ToListAsync())); // expect: NP0005
        app.MapGet("/customers/json-status", async (AppDb db) => Results.Json(await db.Customers.ToListAsync(), statusCode: StatusCodes.Status200OK)); // expect: NP0005
        app.MapGet("/customers/typed-json", async (AppDb db) => TypedResults.Json(await db.Customers.ToListAsync(), contentType: "application/json")); // expect: NP0005
        app.MapGet("/customers/json-names", async (AppDb db) => Results.Json(await db.Customers.Select(c => c.Name).ToListAsync(), statusCode: StatusCodes.Status200OK));
        app.MapGet("/customers/json-included", async (AppDb db)
            => Results.Json(await db.Customers.Include(c => c.Address).Include(c => c.Orders).ThenInclude(o => o.Lines).ThenInclude(l => l.Product).ToListAsync(), statusCode: StatusCodes.Status200OK));
    }
}
