using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Corpus.Lazy;

// Navigations that are loaded, or never lazily loaded, without an Include in the query.
public class Invoice
{
    public int Id { get; set; }
    public virtual Customer Customer { get; set; }
    public virtual Address BillingAddress { get; set; }
    public virtual List<InvoiceLine> Lines { get; set; } = new();
}

public class InvoiceLine
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public virtual Product Product { get; set; }
}

public class InvoiceDb : DbContext
{
    public InvoiceDb(DbContextOptions<InvoiceDb> options) : base(options) { }
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>().Navigation(i => i.Customer).AutoInclude();
        modelBuilder.ApplyConfiguration(new InvoiceLineConfiguration());
    }
}

public class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder) => builder.Navigation(l => l.Product).AutoInclude();
}

public class LoadedWithoutInclude
{
    private readonly AppDb _db;
    private readonly InvoiceDb _invoices;

    public LoadedWithoutInclude(AppDb db, InvoiceDb invoices)
    {
        _db = db;
        _invoices = invoices;
    }

    public async Task AutoIncluded()
    {
        var list = await _invoices.Invoices.Include(i => i.Lines).ToListAsync();
        foreach (var i in list)
        {
            Console.WriteLine(i.Customer.Name);
            Console.WriteLine(i.BillingAddress.City); // expect: NP0002
            foreach (var l in i.Lines)
            {
                Console.WriteLine(l.Product.Name);
            }
        }
    }

    public async Task AutoIncludesIgnored()
    {
        var list = await _invoices.Invoices.IgnoreAutoIncludes().ToListAsync();
        foreach (var i in list)
        {
            Console.WriteLine(i.Customer.Name); // expect: NP0002
        }
    }

    public async Task NoTracking()
    {
        var list = await _db.Orders.AsNoTracking().ToListAsync();
        foreach (var o in list)
        {
            Console.WriteLine(o.Customer?.Name); // expect: NP0002
        }

        var resolved = await _db.Orders.Where(o => o.Total > 0).AsNoTrackingWithIdentityResolution().ToListAsync();
        Console.WriteLine(resolved.Select(o => o.Customer?.Name).Count()); // expect: NP0002
    }

    public async Task TrackingAgain()
    {
        var list = await _db.Orders.AsNoTracking().AsTracking().ToListAsync();
        foreach (var o in list)
        {
            Console.WriteLine(o.Customer.Name); // expect: NP0002
        }
    }

    public async Task LazyLoadingTurnedOff()
    {
        _db.ChangeTracker.LazyLoadingEnabled = false;
        var list = await _db.Orders.ToListAsync();
        foreach (var o in list)
        {
            Console.WriteLine(o.Customer?.Name);
        }
    }
}
