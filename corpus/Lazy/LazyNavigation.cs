using Microsoft.EntityFrameworkCore;

namespace Corpus.Lazy;

// Lazy loading is on in this folder: reading a navigation that was not loaded runs a query.
public class LazyNavigation
{
    private readonly AppDb _db;
    private readonly IOrderRepository _orders;

    public LazyNavigation(AppDb db, IOrderRepository orders)
    {
        _db = db;
        _orders = orders;
    }

    public async Task ReferenceInForeach()
    {
        var orders = await _db.Orders.ToListAsync();
        foreach (var o in orders)
        {
            Console.WriteLine(o.Customer.Name); // expect: NP0002
            Console.WriteLine(o.Customer.Id);
        }
    }

    public async Task<List<string>> ReferenceInSelectLambda()
    {
        var orders = await _db.Orders.ToListAsync();
        return orders.Select(o => o.Customer.Name).ToList(); // expect: NP0002
    }

    public async Task<List<string>> ReferenceThroughMapperMethodGroup()
    {
        var orders = await _db.Orders.ToListAsync();
        return orders.Select(Mapper.CustomerName).ToList(); // expect: NP0002
    }

    public async Task<List<string>> ReferenceThroughMapperCall()
    {
        var orders = await _db.Orders.ToListAsync();
        var names = new List<string>();
        foreach (var o in orders)
        {
            names.Add(Mapper.CustomerName(o)); // expect: NP0002
        }

        return names;
    }

    public async Task<decimal> NestedCollections()
    {
        var customers = await _db.Customers.ToListAsync();
        decimal total = 0;
        foreach (var c in customers)
        {
            foreach (var o in c.Orders) // expect: NP0002
            {
                total += o.Lines.Sum(l => l.Price); // expect: NP0002
            }
        }

        return total;
    }

    public async Task ThroughRepository()
    {
        var orders = await _orders.GetAllAsync();
        foreach (var o in orders)
        {
            Console.WriteLine(o.Customer.Name); // expect: NP0002
        }
    }

    public async Task ForeachDirectlyOverQuery()
    {
        foreach (var o in _db.Orders.Where(x => x.Total > 0))
        {
            Console.WriteLine(o.Customer.Name); // expect: NP0002
        }

        await Task.CompletedTask;
    }

    public async Task DeepReference()
    {
        var customers = await _db.Customers.Include(c => c.Orders).ToListAsync();
        foreach (var c in customers)
        {
            foreach (var o in c.Orders)
            {
                foreach (var l in o.Lines) // expect: NP0002
                {
                    Console.WriteLine(l.Product.Name); // expect: NP0002
                }
            }
        }
    }

    public async Task Included()
    {
        var orders = await _db.Orders.Include(o => o.Customer).ThenInclude(c => c.Address).ToListAsync();
        foreach (var o in orders)
        {
            Console.WriteLine(o.Customer.Name + o.Customer.Address.City);
        }
    }

    public async Task IncludedWithString()
    {
        var orders = await _db.Orders.Include("Customer").Include("Lines.Product").ToListAsync();
        foreach (var o in orders)
        {
            Console.WriteLine(o.Customer.Name);
            foreach (var l in o.Lines)
            {
                Console.WriteLine(l.Product.Name);
            }
        }
    }

    public async Task SingleEntityOutsideLoop(int id)
    {
        var order = await _db.Orders.FirstAsync(o => o.Id == id);
        Console.WriteLine(order.Customer.Name);
    }

    public List<string> ProjectedInsideExpressionTree()
        => _db.Orders.Select(o => o.Customer.Name).ToList();

    public async Task AssigningANavigationDoesNotLoadIt(Customer customer)
    {
        var orders = await _db.Orders.ToListAsync();
        foreach (var o in orders)
        {
            o.Customer = customer;
        }
    }

    public void ElementsOfUnknownOrigin(List<Order> orders)
    {
        // ground-truth: where the elements come from decides; the harness passes tracked query results
        foreach (var o in orders)
        {
            Console.WriteLine(o.Customer.Name);
        }
    }

    public async Task ProjectedToAnonymousTypeFirst()
    {
        var rows = await _db.Orders.Select(o => new { o.Id, Name = o.Customer.Name }).ToListAsync();
        foreach (var r in rows)
        {
            Console.WriteLine(r.Name);
        }
    }
}
