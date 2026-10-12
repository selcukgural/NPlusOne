using Microsoft.EntityFrameworkCore;

namespace NPlusOne.EntityFrameworkCore.Tests;

public class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public virtual ICollection<Order> Orders { get; set; } = [];
}

public class Order
{
    public int Id { get; set; }

    public string Number { get; set; } = "";

    public int CustomerId { get; set; }

    public virtual Customer? Customer { get; set; }
}

public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Order> Orders => Set<Order>();
}
