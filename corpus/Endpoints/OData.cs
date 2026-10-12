using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Endpoints;

// OData writes only the structural properties of what an [EnableQuery] action returns; $expand goes into the SQL
// query. The serializer never reads a navigation that was not asked for.
public class ODataOrdersController(AppDb db) : ODataController
{
    [EnableQuery]
    // ground-truth: the harness serializes action results with System.Text.Json; OData's formatter, which writes structural properties only, does not run here
    public IQueryable<Order> Get() => db.Orders;

    [EnableQuery]
    // ground-truth: the harness serializes action results with System.Text.Json; OData's formatter, which writes structural properties only, does not run here
    public async Task<List<Customer>> Customers() => await db.Customers.ToListAsync();

    // Without [EnableQuery] the action is an ordinary one: its result goes through the configured serializer.
    public async Task<List<Order>> Plain() => await db.Orders.ToListAsync(); // expect: NP0005
}

[ApiController]
[Route("odata-like")]
public class EnableQueryOnApiController(AppDb db) : ControllerBase
{
    [HttpGet]
    [EnableQuery]
    // ground-truth: the harness serializes action results with System.Text.Json; OData's formatter, which writes structural properties only, does not run here
    public IQueryable<Order> Get() => db.Orders;
}

// [EnableQuery] on the controller applies to every action.
[EnableQuery]
public class ODataCustomersController(AppDb db) : ODataController
{
    public IQueryable<Customer> Get() => db.Customers; // ground-truth: the harness serializes action results with System.Text.Json; OData's formatter, which writes structural properties only, does not run here
}
