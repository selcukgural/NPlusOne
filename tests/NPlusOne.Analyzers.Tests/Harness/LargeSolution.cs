using System.Globalization;

namespace NPlusOne.Analyzers.Tests.Harness;

/// <summary>
/// Generates a large application for measuring the analyzer: many modules, each with its entities and their
/// configuration (every twentieth with an <c>AutoInclude</c>), a service with N+1 and clean methods, a dispatched
/// handler, a controller, a MediatR request and an AutoMapper profile, plus one startup file with the context and the
/// registrations the whole-compilation walks look for (<c>AddNewtonsoftJson</c>, <c>AddOpenBehavior</c>, <c>MapGet</c>
/// with a method group).
/// </summary>
public static class LargeSolution
{
    /// <summary>What precedes the literal a keystroke edits in each module's service, so the edit keeps the file compiling.</summary>
    public const string EditMarker = "var total = ";

    /// <summary>The sources: one file per module, then the startup file.</summary>
    /// <param name="modules">How many modules.</param>
    public static IReadOnlyList<TestCompilation.Source> Sources(int modules)
    {
        var sources = new List<TestCompilation.Source>(modules + 1);
        for (var i = 0; i < modules; i++)
        {
            sources.Add(new TestCompilation.Source($"Module{i}.cs", Module(i)));
        }

        sources.Add(new TestCompilation.Source("Startup.cs", Startup(modules)));
        return sources;
    }

    /// <summary>The path of a module's file.</summary>
    /// <param name="module">The module number.</param>
    public static string ModulePath(int module) => $"Module{module}.cs";

    private static string Module(int i) => $$"""
        using AutoMapper;
        using MediatR;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.Metadata.Builders;

        namespace Shop.M{{i}};

        public class OrderConfiguration : IEntityTypeConfiguration<Order>
        {
            public void Configure(EntityTypeBuilder<Order> builder)
            {
                builder.HasKey(o => o.Id);
                builder.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId);
                {{(i % 20 == 0 ? "builder.Navigation(o => o.Customer).AutoInclude();" : "")}}
            }
        }

        public class Order
        {
            public int Id { get; set; }
            public int CustomerId { get; set; }
            public virtual Customer Customer { get; set; }
            public virtual List<Line> Lines { get; set; } = new();
        }

        public class Customer
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public virtual List<Order> Orders { get; set; } = new();
        }

        public class Line
        {
            public int Id { get; set; }
            public int OrderId { get; set; }
            public decimal Price { get; set; }
        }

        public class OrderView
        {
            public int Id { get; set; }
            public string CustomerName { get; set; }
        }

        public interface ILinePricer
        {
            Task<decimal> PriceAsync(int orderId);
        }

        public class LinePricer(Shop.ShopContext db) : ILinePricer
        {
            public Task<decimal> PriceAsync(int orderId) => db.M{{i}}Lines.Where(l => l.OrderId == orderId).SumAsync(l => l.Price);
        }

        public class OrderService(Shop.ShopContext db, ILinePricer pricer, IMapper mapper)
        {
            public async Task<int> CountLinesAsync()
            {
                var total = 0;
                foreach (var order in await db.M{{i}}Orders.ToListAsync())
                {
                    total += await db.M{{i}}Lines.CountAsync(l => l.OrderId == order.Id);
                }

                return total;
            }

            public async Task<decimal> PriceAllAsync()
            {
                decimal sum = 0;
                foreach (var order in await db.M{{i}}Orders.ToListAsync())
                {
                    sum += await pricer.PriceAsync(order.Id);
                }

                return sum;
            }

            public List<string> Names() => db.M{{i}}Orders.Include(o => o.Customer).Select(o => o.Customer.Name).ToList();

            public List<OrderView> Views() => mapper.Map<List<OrderView>>(db.M{{i}}Orders.ToList());

            public decimal Sum(Order order) => Add(order.Lines);

            private static decimal Add(IEnumerable<Line> lines)
            {
                decimal sum = 0;
                foreach (var line in lines)
                {
                    sum += line.Price;
                }

                return sum;
            }

            public async Task<Dictionary<int, int>> CountsAsync()
            {
                var ids = await db.M{{i}}Orders.Select(o => o.Id).ToListAsync();
                var counts = await db.M{{i}}Lines.Where(l => ids.Contains(l.OrderId)).GroupBy(l => l.OrderId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
                return counts.ToDictionary(c => c.Key, c => c.Count);
            }
        }

        public record CountOrders(int CustomerId) : IRequest<int>;

        public class CountOrdersHandler(Shop.ShopContext db) : IRequestHandler<CountOrders, int>
        {
            public Task<int> Handle(CountOrders request, CancellationToken cancellationToken)
                => db.M{{i}}Orders.CountAsync(o => o.CustomerId == request.CustomerId, cancellationToken);
        }

        [ApiController]
        [Route("m{{i}}/orders")]
        public class OrdersController(Shop.ShopContext db, ISender sender) : ControllerBase
        {
            [HttpGet]
            public IActionResult Get() => Ok(db.M{{i}}Orders.Include(o => o.Lines).ToList());

            [HttpGet("counts")]
            public async Task<List<int>> Counts()
            {
                var counts = new List<int>();
                foreach (var customer in await db.M{{i}}Customers.ToListAsync())
                {
                    counts.Add(await sender.Send(new CountOrders(customer.Id)));
                }

                return counts;
            }
        }

        public class OrderProfile : Profile
        {
            public OrderProfile() => CreateMap<Order, OrderView>();
        }
        """;

    private static string Startup(int modules)
    {
        var sets = new System.Text.StringBuilder();
        for (var i = 0; i < modules; i++)
        {
            sets.AppendLine(CultureInfo.InvariantCulture, $"    public DbSet<Shop.M{i}.Order> M{i}Orders {{ get; set; }}");
            sets.AppendLine(CultureInfo.InvariantCulture, $"    public DbSet<Shop.M{i}.Customer> M{i}Customers {{ get; set; }}");
            sets.AppendLine(CultureInfo.InvariantCulture, $"    public DbSet<Shop.M{i}.Line> M{i}Lines {{ get; set; }}");
        }

        return $$"""
            using MediatR;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.EntityFrameworkCore;
            using Microsoft.Extensions.DependencyInjection;

            namespace Shop;

            public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
            {
            {{sets}}
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                    => modelBuilder.ApplyConfigurationsFromAssembly(typeof(ShopContext).Assembly);
            }

            public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
                where TRequest : notnull
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services, WebApplication app)
                {
                    services.AddControllers().AddNewtonsoftJson();
                    services.AddMediatR(configuration => configuration.AddOpenBehavior(typeof(LoggingBehavior<,>)));
                    app.MapGet("/orders", Handlers.GetOrders);
                }
            }

            public static class Handlers
            {
                public static IResult GetOrders(ShopContext db) => Results.Ok(db.M0Orders.ToList());
            }
            """;
    }
}
