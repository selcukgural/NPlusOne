// The model every corpus folder compiles against. Navigations are virtual so that lazy-loading
// proxies can load them; whether lazy loading is on is decided per folder.
using Microsoft.EntityFrameworkCore;

namespace Corpus;

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int? AddressId { get; set; }
    public virtual Address Address { get; set; }
    public virtual List<Order> Orders { get; set; } = new();
}

public class Address
{
    public int Id { get; set; }
    public string City { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public decimal Total { get; set; }
    public virtual Customer Customer { get; set; }
    public virtual List<OrderLine> Lines { get; set; } = new();
}

public class OrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public decimal Price { get; set; }
    public virtual Product Product { get; set; }
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public class Category
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public string Name { get; set; }
    public virtual Category Parent { get; set; }
}

public class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();

    // A query exposed as a property: its getter returns an EF-backed query.
    public IQueryable<Order> BigOrders => Orders.Where(o => o.Total > 1000);
}

public interface IOrderRepository
{
    Task<List<Order>> GetByCustomerAsync(int customerId);
    Task<List<Order>> GetAllAsync();
}

public class OrderRepository : IOrderRepository
{
    private readonly AppDb _db;
    public OrderRepository(AppDb db) => _db = db;
    public Task<List<Order>> GetByCustomerAsync(int customerId) => _db.Orders.Where(o => o.CustomerId == customerId).ToListAsync();
    public Task<List<Order>> GetAllAsync() => _db.Orders.ToListAsync();
}

// An abstraction whose implementation the analyzer cannot see, as in a Clean Architecture
// application project that does not reference the infrastructure project.
public interface ICustomerRepository
{
    Task<Customer> GetByIdAsync(int id);
    Task<int> CountOrdersAsync(int customerId);
    void Remember(Customer customer);
}

public interface IPriceCalculator
{
    decimal Calculate(Order order);
}

public static class Mapper
{
    public static string CustomerName(Order order) => order.Customer.Name;
    public static decimal Sum(Order order) => order.Total;
}
