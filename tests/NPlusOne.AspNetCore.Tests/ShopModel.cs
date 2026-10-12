using Microsoft.EntityFrameworkCore;

namespace NPlusOne.AspNetCore.Tests;

public class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public List<Order> Orders { get; set; } = [];
}

public class Order
{
    public int Id { get; set; }

    public int CustomerId { get; set; }
}

public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Order> Orders => Set<Order>();

    /// The N+1 every endpoint below uses: one query per customer.
    public async Task<int> CountOrdersOneByOneAsync()
    {
        var total = 0;
        foreach (var customer in await Customers.ToListAsync())
        {
            total += await Orders.CountAsync(o => o.CustomerId == customer.Id);
        }

        return total;
    }

    public async Task<int> CountOrdersAtOnceAsync() =>
        (await Customers.Include(c => c.Orders).ToListAsync()).Sum(c => c.Orders.Count);
}
