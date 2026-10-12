using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Endpoints;

// The catalogue's "works" rows on endpoints: other result helpers, a query returned as is, entities returned through a
// service, a query per element inside a handler, and a Razor page model whose markup decides what is read.
public class OrderListService(AppDb db)
{
    public Task<List<Order>> AllAsync() => db.Orders.ToListAsync();
}

[ApiController]
[Route("results")]
public class ResultHelpersController(AppDb db, OrderListService service) : ControllerBase
{
    [HttpGet("{id}")]
    // One order: its lines are one query, each line's product one more.
    public async Task<IActionResult> Get(int id) => Ok(await db.Orders.FindAsync(id)); // expect: NP0005

    [HttpPost("created-at-action")]
    public async Task<IActionResult> CreatedAtActionResult() => CreatedAtAction(nameof(Get), new { id = 1 }, await db.Orders.ToListAsync()); // expect: NP0005

    [HttpPost("created")]
    public async Task<IActionResult> CreatedResult() => Created("/results", await db.Orders.ToListAsync()); // expect: NP0005

    [HttpPost("accepted")]
    public async Task<IActionResult> AcceptedResult() => Accepted(await db.Orders.ToListAsync()); // expect: NP0005

    [HttpGet("query")]
    public IQueryable<Order> Query() => db.Orders; // expect: NP0005

    [HttpGet("service")]
    public async Task<List<Order>> ThroughService() => await service.AllAsync(); // expect: NP0005
}

public static class HandlerQueries
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/orders/found", async (AppDb db) =>
        {
            var found = 0;
            foreach (var id in await db.Orders.Select(o => o.Id).ToListAsync())
            {
                if (await db.Orders.FindAsync(id) is not null) // expect: NP0001
                {
                    found++;
                }
            }

            return found;
        });
    }
}

// The page's markup reads what it reads; assigning the property loads nothing more.
public class OrdersPageModel(AppDb db) : PageModel
{
    public List<Order> Orders { get; set; } = [];

    public async Task OnGetAsync() => Orders = await db.Orders.ToListAsync();
}
