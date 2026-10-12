using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Endpoints;

// Serializing entities outside an endpoint reads every navigation the same way.
public class Serialization
{
    // Entities refer to each other (Order.Customer, Customer.Orders); serializing them needs cycles ignored.
    private static readonly JsonSerializerOptions Json = new() { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles };

    private readonly AppDb _db;
    private readonly IHubContext<OrdersHub> _hub;

    public Serialization(AppDb db, IHubContext<OrdersHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    public async Task<string> ToJson()
    {
        var orders = await _db.Orders.ToListAsync();
        return JsonSerializer.Serialize(orders, Json); // expect: NP0005
    }

    public async Task ToStream(Stream stream)
    {
        var customers = await _db.Customers.ToListAsync();
        await JsonSerializer.SerializeAsync(stream, customers, Json); // expect: NP0005
    }

    public async Task<string> ToJsonWithNewtonsoft()
    {
        var orders = await _db.Orders.ToListAsync();
        return Newtonsoft.Json.JsonConvert.SerializeObject(orders, new Newtonsoft.Json.JsonSerializerSettings { ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore }); // expect: NP0005
    }

    public async Task Broadcast()
    {
        var orders = await _db.Orders.ToListAsync();
        await _hub.Clients.All.SendAsync("orders", orders); // expect: NP0005
    }

    public async Task<string> UntrackedToJson()
    {
        var orders = await _db.Orders.AsNoTracking().ToListAsync();
        return JsonSerializer.Serialize(orders, Json); // expect: NP0005
    }

    public async Task<string> ProjectedToJson()
    {
        var rows = await _db.Orders.Select(o => new { o.Id, Customer = o.Customer.Name }).ToListAsync();
        return JsonSerializer.Serialize(rows, Json);
    }

    public async Task<string> FullyIncludedToJson()
    {
        var lines = await _db.OrderLines.Include(l => l.Product).ToListAsync();
        return JsonSerializer.Serialize(lines, Json);
    }
}

public class OrdersHub : Hub;
