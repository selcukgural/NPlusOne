using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Endpoints;

// Strongly typed SignalR clients serialize their arguments like SendAsync; hub methods return values
// to the caller through the same serializer.
public interface IOrderClient
{
    Task OrdersChanged(List<Order> orders);
    Task OrderNamesChanged(List<string> names);
}

public class TypedOrdersHub(AppDb db) : Hub<IOrderClient>
{
    public async Task Refresh()
    {
        var orders = await db.Orders.ToListAsync();
        await Clients.Caller.OrdersChanged(orders); // expect: NP0005
    }

    public async Task RefreshNames()
    {
        var names = await db.Orders.Select(o => o.Customer.Name).ToListAsync();
        await Clients.Others.OrderNamesChanged(names);
    }

    public async Task<List<Order>> GetOrders() => await db.Orders.ToListAsync(); // expect: NP0005

    // Customer.Orders is not loaded by including Customer: the serializer queries it once per customer.
    public async Task<List<Order>> GetOrdersIncluded()
        => await db.Orders.Include(o => o.Customer).ThenInclude(c => c.Address).Include(o => o.Lines).ThenInclude(l => l.Product).ToListAsync(); // expect: NP0005

    private Task<List<Order>> NotCallable() => db.Orders.ToListAsync();
}

public class TypedBroadcaster(AppDb db, IHubContext<TypedOrdersHub, IOrderClient> hub)
{
    public async Task Broadcast()
    {
        var orders = await db.Orders.ToListAsync();
        await hub.Clients.All.OrdersChanged(orders); // expect: NP0005
    }

    public async Task BroadcastToGroup(string group)
    {
        var orders = await db.Orders.AsNoTracking().ToListAsync();
        await hub.Clients.Group(group).OrdersChanged(orders); // expect: NP0005
    }
}

// A Razor view reads what its markup reads, not every property: handing entities to a view is not
// serialization (the view's own reads are what load lazily).
public class OrdersPageController(AppDb db) : Controller
{
    public async Task<IActionResult> Index() => View(await db.Orders.ToListAsync());

    public async Task<IActionResult> Partial() => PartialView("_Orders", await db.Orders.ToListAsync());

    public async Task<IActionResult> AsJson() => Json(await db.Orders.ToListAsync()); // expect: NP0005

    public async Task<IActionResult> AsJsonWithOptions() => Json(await db.Orders.ToListAsync(), new JsonSerializerOptions()); // expect: NP0005

    public async Task<IActionResult> NamesAsJsonWithOptions() => Json(await db.Orders.Select(o => o.Customer.Name).ToListAsync(), new JsonSerializerOptions());

    public async Task<IActionResult> AsObjectResult() => new OkObjectResult(await db.Orders.ToListAsync()); // expect: NP0005

    public async Task<IActionResult> AsJsonResult() => new JsonResult(await db.Customers.ToListAsync()); // expect: NP0005

    public IAsyncEnumerable<Order> Streamed() => db.Orders.AsAsyncEnumerable(); // expect: NP0005

    public IQueryable<Order> Queryable() => db.Orders.AsQueryable(); // expect: NP0005

    // Everything included: the orders' Customer back-reference is fixed up from the customers being loaded.
    public async Task<IActionResult> CustomersFullyIncluded()
        => Ok(await db.Customers.Include(c => c.Address).Include(c => c.Orders).ThenInclude(o => o.Lines).ThenInclude(l => l.Product).ToListAsync());
}

public static class MethodGroupEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/orders", GetOrders);
        api.MapGet("/orders/untracked", GetUntrackedOrders);
    }

    private static async Task<List<Order>> GetOrders(AppDb db) => await db.Orders.ToListAsync(); // expect: NP0005

    private static async Task<List<Order>> GetUntrackedOrders(AppDb db) => await db.Orders.AsNoTracking().ToListAsync(); // expect: NP0005
}
