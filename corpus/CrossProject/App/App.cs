using Microsoft.EntityFrameworkCore;
using Corpus.Data;

namespace Corpus.App;

// An application project that references the data project: every call below goes into metadata.
public class Service(AppDb db, OrderStore repository, IOrderReader reader, TicketDb tickets)
{
    public async Task RepositoryPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            var orders = await repository.GetByCustomerAsync(id); // expect: NP0001
            var count = repository.CachedCount();
        }
    }

    public async Task InterfaceImplementedInReferencedProject(List<int> ids)
    {
        foreach (var id in ids)
        {
            var order = await reader.Get(id); // expect: NP0001
        }
    }

    public async Task QueryDecidedByTheArgument(List<int> ids, List<Order> cached)
    {
        foreach (var id in ids)
        {
            var fromDb = Queries.CountIn(db.Orders, id); // expect: NP0001
            var fromMemory = Queries.CountIn(cached.AsQueryable(), id);
        }
    }

    public async Task SavePerElement(List<Order> orders)
    {
        foreach (var o in orders)
        {
            await repository.Save(o); // expect: NP0003
        }
    }

    public async Task ElementsReturnedByTheRepository()
    {
        var orders = await repository.GetAllAsync();
        foreach (var o in orders)
        {
            Console.WriteLine(o.Customer.Name); // expect: NP0002
        }

        var included = await repository.GetAllWithCustomerAsync();
        foreach (var o in included)
        {
            Console.WriteLine(o.Customer.Name);
        }
    }

    public async Task MapperAndPrinterInTheDataProject()
    {
        var orders = await db.Orders.ToListAsync();
        foreach (var o in orders)
        {
            Console.WriteLine(OrderMapper.CustomerName(o)); // expect: NP0002
        }

        var other = await db.Orders.Where(o => o.Total > 0).ToListAsync();
        OrderPrinter.PrintCustomers(other); // expect: NP0002
    }

    public async Task AutoIncludeConfiguredInTheDataProject()
    {
        var list = await tickets.Tickets.ToListAsync();
        foreach (var t in list)
        {
            Console.WriteLine(t.Customer.Name);
            Console.WriteLine(t.Assignee.Name); // expect: NP0002
        }
    }
}
