using Microsoft.EntityFrameworkCore;

namespace Corpus.Data;

// A data project: repositories, helpers and a context the application project only sees as metadata. Its assembly name
// starts with "System" on purpose: only System and System.* are framework assemblies, SystemData is the application's.
public interface IOrderReader
{
    Task<Order> Get(int id);
}

public class OrderReader(AppDb db) : IOrderReader
{
    public Task<Order> Get(int id) => db.Orders.FirstAsync(o => o.Id == id);
}

public class OrderStore(AppDb db)
{
    private readonly List<Order> _cache = [];

    public Task<List<Order>> GetByCustomerAsync(int customerId) => db.Orders.Where(o => o.CustomerId == customerId).ToListAsync();

    public Task<List<Order>> GetAllAsync() => db.Orders.ToListAsync();

    public Task<List<Order>> GetAllWithCustomerAsync() => db.Orders.Include(o => o.Customer).ToListAsync();

    public int CachedCount() => _cache.Count;

    public async Task Save(Order order)
    {
        db.Update(order);
        await db.SaveChangesAsync();
    }
}

public static class Queries
{
    public static int CountIn(IQueryable<Order> source, int customerId) => source.Count(o => o.CustomerId == customerId);
}

public static class OrderMapper
{
    public static string CustomerName(Order order) => order.Customer.Name;
}

public static class OrderPrinter
{
    public static void PrintCustomers(IEnumerable<Order> orders)
    {
        // ground-truth: a helper given its elements; reported at the call that passes a query result
        foreach (var o in orders)
        {
            Console.WriteLine(o.Customer.Name);
        }
    }
}

public class Ticket
{
    public int Id { get; set; }
    public virtual Customer Customer { get; set; }
    public virtual Customer Assignee { get; set; }
}

public class TicketDb(DbContextOptions<TicketDb> options) : DbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Ticket>().Navigation(t => t.Customer).AutoInclude();
}
