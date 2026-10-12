using Microsoft.EntityFrameworkCore;

namespace Corpus.Lazy;

// The catalogue's "works" rows on lazy loading, each kept here as a case: filtered includes and split queries load what
// they include, owned types come with their owner, skip navigations load lazily like any collection, and the read
// shapes (null-conditional, query syntax, string Include) behave like plain reads.
public class Consignment
{
    public int Id { get; set; }
    public ConsignmentPlace Origin { get; set; }
}

public class ConsignmentPlace
{
    public string City { get; set; }
}

public class Post
{
    public int Id { get; set; }
    public string Title { get; set; }
    public virtual List<Tag> Tags { get; set; } = new();
}

public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; }
    public virtual List<Post> Posts { get; set; } = new();
}

public class BlogDb(DbContextOptions<BlogDb> options) : DbContext(options)
{
    public DbSet<Consignment> Consignments => Set<Consignment>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Tag> Tags => Set<Tag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Consignment>().OwnsOne(s => s.Origin);
}

public class WorksLazy(AppDb db, BlogDb blog)
{
    public async Task<int> FilteredInclude()
    {
        var total = 0;
        foreach (var order in await db.Orders.Include(o => o.Lines.Where(l => l.Price >= 0)).ToListAsync())
        {
            total += order.Lines.Count;
        }

        return total;
    }

    public async Task<int> SplitQuery()
    {
        var total = 0;
        foreach (var customer in await db.Customers.Include(c => c.Orders).ThenInclude(o => o.Lines).AsSplitQuery().ToListAsync())
        {
            foreach (var order in customer.Orders)
            {
                total += order.Lines.Count;
            }
        }

        return total;
    }

    public async Task<int> OwnedType()
    {
        var total = 0;
        foreach (var consignment in await blog.Consignments.ToListAsync())
        {
            total += consignment.Origin?.City?.Length ?? 0;
        }

        return total;
    }

    public async Task<int> SkipNavigation()
    {
        var total = 0;
        foreach (var post in await blog.Posts.ToListAsync())
        {
            total += post.Tags.Count; // expect: NP0002
        }

        return total;
    }

    public async Task<int> SkipNavigationIncluded()
    {
        var total = 0;
        foreach (var post in await blog.Posts.Include(p => p.Tags).ToListAsync())
        {
            total += post.Tags.Count;
        }

        return total;
    }

    public async Task<int> NullConditional()
    {
        var total = 0;
        foreach (var order in await db.Orders.ToListAsync())
        {
            total += order.Customer?.Address?.City?.Length ?? 0; // expect: NP0002
        }

        return total;
    }

    public async Task<int> QuerySyntax()
    {
        var orders = await db.Orders.ToListAsync();
        var names = from o in orders
                    select o.Customer.Name; // expect: NP0002
        return names.Sum(n => n.Length);
    }

    public async Task<int> StringInclude()
    {
        var total = 0;
        foreach (var order in await db.Orders.Include("Customer").ToListAsync())
        {
            total += order.Customer.Name.Length;
        }

        return total;
    }
}
