using NPlusOne.Analyzers.CodeFixes;
using NPlusOne.Analyzers.Tests.Harness;

namespace NPlusOne.Analyzers.Tests;

public class CodeFixTests
{
    private const string Model = """
        using Microsoft.EntityFrameworkCore;

        namespace App;

        public class Customer { public int Id { get; set; } public string Name { get; set; } public virtual Address Address { get; set; } }
        public class Address { public int Id { get; set; } public string City { get; set; } }
        public class Order { public int Id { get; set; } public virtual Customer Customer { get; set; } public virtual List<Line> Lines { get; set; } }
        public class Line { public int Id { get; set; } public virtual Product Product { get; set; } }
        public class Product { public int Id { get; set; } public string Name { get; set; } }

        public class Db(DbContextOptions<Db> options) : DbContext(options)
        {
            public DbSet<Order> Orders => Set<Order>();
        }
        """;

    [Fact]
    public async Task Include_is_added_to_the_query_that_produced_the_elements()
    {
        var result = await CodeFixRunner.ApplyAsync(new AddIncludeCodeFix(), "NP0002", ("Model.cs", Model), ("Service.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class Service(Db db)
            {
                public async Task Run()
                {
                    var orders = await db.Orders.Where(o => o.Id > 0).ToListAsync();
                    foreach (var o in orders)
                    {
                        Console.WriteLine(o.Customer.Name);
                    }
                }
            }
            """));

        Assert.Contains("await db.Orders.Include(x => x.Customer).Where(o => o.Id > 0).ToListAsync();", result.Files["Service.cs"]);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "NP0002");
    }

    [Fact]
    public async Task Include_goes_after_FromSql()
    {
        var result = await CodeFixRunner.ApplyAsync(new AddIncludeCodeFix(), "NP0002", ("Model.cs", Model), ("Service.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class Service(Db db)
            {
                public void Run()
                {
                    var orders = db.Orders.FromSqlRaw("SELECT * FROM Orders").ToList();
                    foreach (var o in orders)
                    {
                        Console.WriteLine(o.Customer.Name);
                    }
                }
            }
            """));

        Assert.Contains("db.Orders.FromSqlRaw(\"SELECT * FROM Orders\").Include(x => x.Customer).ToList();", result.Files["Service.cs"]);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "NP0002");
    }

    [Fact]
    public async Task Nested_paths_become_ThenInclude_and_the_EF_using_is_added()
    {
        var result = await CodeFixRunner.ApplyAsync(new AddIncludeCodeFix(), "NP0002", ("Model.cs", Model), ("Service.cs", """
            namespace App;

            public class Service(Db db)
            {
                public void Run()
                {
                    var orders = db.Orders.Where(o => o.Id > 0).ToList();
                    foreach (var o in orders)
                    {
                        Console.WriteLine(o.Customer.Address.City);
                    }
                }
            }
            """));

        var service = result.Files["Service.cs"];
        Assert.Contains("db.Orders.Include(x => x.Customer).ThenInclude(x => x.Address).Where(o => o.Id > 0).ToList();", service);
        Assert.StartsWith("using Microsoft.EntityFrameworkCore;", service);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "NP0002");
    }

    [Fact]
    public async Task Include_goes_into_the_repository_in_another_file()
    {
        var result = await CodeFixRunner.ApplyAsync(new AddIncludeCodeFix(), "NP0002", ("Model.cs", Model), ("Repository.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class OrderRepository(Db db)
            {
                public Task<List<Order>> All() => db.Orders.ToListAsync();
            }
            """), ("Service.cs", """
            namespace App;

            public class Service(OrderRepository repository)
            {
                public async Task<List<string>> Names() => (await repository.All()).Select(o => o.Customer.Name).ToList();
            }
            """));

        Assert.Contains("db.Orders.Include(x => x.Customer).ToListAsync()", result.Files["Repository.cs"]);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "NP0002");
    }

    [Fact]
    public async Task SaveChanges_moves_after_the_loop()
    {
        var result = await CodeFixRunner.ApplyAsync(new MoveSaveChangesCodeFix(), "NP0003", ("Model.cs", Model), ("Service.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class Service(Db db)
            {
                public async Task Run(List<Order> orders)
                {
                    foreach (var o in orders)
                    {
                        db.Update(o);
                        await db.SaveChangesAsync();
                    }

                    Console.WriteLine("done");
                }
            }
            """));

        var service = result.Files["Service.cs"].Replace("\r\n", "\n");
        Assert.Contains("""
                    foreach (var o in orders)
                    {
                        db.Update(o);
                    }
                    await db.SaveChangesAsync();
            """, service);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "NP0003");
    }

    [Fact]
    public async Task SaveChanges_in_a_callback_is_not_moved()
    {
        var result = await CodeFixRunner.ApplyAsync(new MoveSaveChangesCodeFix(), "NP0003", ("Model.cs", Model), ("Service.cs", """
            namespace App;

            public class Service(Db db)
            {
                public void Run(List<Order> orders) => orders.ForEach(o => { db.Update(o); db.SaveChanges(); });
            }
            """));

        Assert.False(result.Offered);
    }

    [Fact]
    public async Task SaveChanges_on_a_context_created_per_element_is_not_moved()
    {
        var result = await CodeFixRunner.ApplyAsync(new MoveSaveChangesCodeFix(), "NP0003", ("Model.cs", Model), ("Service.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class Service(Func<Db> create)
            {
                public void Run(List<Order> orders)
                {
                    foreach (var o in orders)
                    {
                        using var db = create();
                        db.Update(o);
                        db.SaveChanges();
                    }
                }
            }
            """));

        Assert.Contains(result.Diagnostics, d => d.Id == "NP0003");
        Assert.False(result.Offered);
    }

    [Fact]
    public async Task SaveChanges_in_a_while_loop_is_not_moved()
    {
        var result = await CodeFixRunner.ApplyAsync(new MoveSaveChangesCodeFix(), "NP0003", ("Model.cs", Model), ("Service.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class Service(Db db)
            {
                public void Run(Queue<Order> orders)
                {
                    while (orders.Count > 0)
                    {
                        db.Update(orders.Dequeue());
                        db.SaveChanges();
                    }
                }
            }
            """));

        Assert.Contains(result.Diagnostics, d => d.Id == "NP0003");
        Assert.False(result.Offered);
    }

    [Fact]
    public async Task A_transaction_per_element_is_not_moved()
    {
        var result = await CodeFixRunner.ApplyAsync(new MoveSaveChangesCodeFix(), "NP0003", ("Model.cs", Model), ("Service.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace App;

            public class Service(Db db)
            {
                public void Run(List<Order> orders)
                {
                    foreach (var o in orders)
                    {
                        using var transaction = db.Database.BeginTransaction();
                        db.Update(o);
                        transaction.Commit();
                    }
                }
            }
            """));

        Assert.Contains(result.Diagnostics, d => d.Id == "NP0003");
        Assert.False(result.Offered);
    }
}
