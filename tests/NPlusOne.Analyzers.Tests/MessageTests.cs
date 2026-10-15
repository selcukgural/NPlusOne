using System.Globalization;
using NPlusOne.Analyzers.Tests.Harness;

namespace NPlusOne.Analyzers.Tests;

// Messages name the iteration a round trip repeats in. The names are built when a message needs them, from the loop or
// lambda, so these check that each kind of iteration is still named as it was found.
public class MessageTests
{
    private const string Shop = """
        using FluentValidation;
        using Microsoft.EntityFrameworkCore;

        namespace Shop;

        public class Customer
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class Order
        {
            public int Id { get; set; }
            public int CustomerId { get; set; }
            public virtual Customer Customer { get; set; }
        }

        public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
        {
            public DbSet<Order> Orders { get; set; }
            public DbSet<Customer> Customers { get; set; }
        }

        public class Reports(ShopDb db)
        {
            public int Loop(List<int> ids)
            {
                var total = 0;
                foreach (var id in ids)
                {
                    total += db.Orders.Count(o => o.CustomerId == id);
                }

                return total;
            }

            public List<int> Lambda(List<int> ids) => ids.Select(id => db.Orders.Count(o => o.CustomerId == id)).ToList();

            public void Parallel(List<int> ids) => System.Threading.Tasks.Parallel.ForEach(ids, id => db.Orders.Count(o => o.CustomerId == id));

            public int Lazy()
            {
                var total = 0;
                foreach (var order in db.Orders.ToList())
                {
                    total += order.Customer.Name.Length;
                }

                return total;
            }

            public List<int> LazyInLambda() => db.Orders.ToList().Select(o => o.Customer.Name.Length).ToList();
        }

        public class Basket
        {
            public List<int> CustomerIds { get; set; } = [];
        }

        public class BasketValidator : AbstractValidator<Basket>
        {
            public BasketValidator(ShopDb db) => RuleForEach(b => b.CustomerIds).Must(id => db.Customers.Any(c => c.Id == id));
        }
        """;

    [Theory]
    [InlineData("Loop", "the foreach loop over 'ids'")]
    [InlineData("Lambda", "the 'Select' lambda")]
    [InlineData("Parallel", "the 'Parallel.ForEach' callback")]
    [InlineData("Lazy", "the foreach loop over 'db.Orders.ToList()'")]
    [InlineData("LazyInLambda", "the 'Select' lambda")]
    [InlineData("BasketValidator", "the 'Must' rule chained on RuleForEach, which FluentValidation runs for each item")]
    public async Task Each_iteration_is_named_in_the_message(string member, string iteration)
    {
        var diagnostics = await TestCompilation.AnalyzeAsync([new TestCompilation.Source("Shop.cs", Shop)]);
        var lines = Shop.Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains($" {member}(", StringComparison.Ordinal));

        var messages = diagnostics
            .Where(d => d.Location.GetLineSpan().StartLinePosition.Line >= start)
            .OrderBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture));
        Assert.Contains($"once per element of {iteration}", messages.First(), StringComparison.Ordinal);
    }
}
