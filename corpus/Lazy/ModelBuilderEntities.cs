using Microsoft.EntityFrameworkCore;

namespace Corpus.Lazy;

// A context without DbSet properties: its entities are mapped in OnModelCreating only, with Entity<T>() (also through an
// extension method on ModelBuilder, as ABP modules configure theirs) or Entity(typeof(T)), and queried with Set<T>().
public class Ledger
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public virtual LedgerOwner Owner { get; set; }
}

public class LedgerOwner
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public class Voucher
{
    public int Id { get; set; }
    public int IssuerId { get; set; }
    public virtual VoucherIssuer Issuer { get; set; }
}

public class VoucherIssuer
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public class Receipt
{
    public int Id { get; set; }
    public int ClerkId { get; set; }
    public virtual ReceiptClerk Clerk { get; set; }
}

public class ReceiptClerk
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public class LedgerDb(DbContextOptions<LedgerDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ledger>(b => b.HasOne(l => l.Owner).WithMany().HasForeignKey(l => l.OwnerId));
        modelBuilder.ConfigureVouchers();
        modelBuilder.Entity(typeof(Receipt));
    }
}

public static class VoucherModel
{
    public static void ConfigureVouchers(this ModelBuilder builder) => builder.Entity<Voucher>();
}

// A report card built in memory, registered with something that is not EF Core's model: it is not an entity.
public class LedgerCard
{
    public int Id { get; set; }
    public LedgerOwner Owner { get; set; }
}

public class ReportRegistry
{
    public ReportRegistry Entity<T>() => this;

    public static ReportRegistry Configure() => new ReportRegistry().Entity<LedgerCard>();
}

public interface ILedgerCardSource
{
    Task<IQueryable<LedgerCard>> GetCardsAsync();
}

public class ModelBuilderEntities(LedgerDb db)
{
    public async Task<int> ConfiguredEntityNavigationPerElement()
    {
        var total = 0;
        foreach (var ledger in await db.Set<Ledger>().ToListAsync())
        {
            total += ledger.Owner.Name.Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> ConfiguredEntityNavigationIncluded()
    {
        var total = 0;
        foreach (var ledger in await db.Set<Ledger>().Include(l => l.Owner).ToListAsync())
        {
            total += ledger.Owner.Name.Length;
        }

        return total;
    }

    public async Task<int> ConfiguredEntityScalarsPerElement()
    {
        var total = 0;
        foreach (var ledger in await db.Set<Ledger>().ToListAsync())
        {
            total += ledger.OwnerId;
        }

        return total;
    }

    public async Task<int> CardsFromAnotherSource(ILedgerCardSource source, List<int> ids)
    {
        var total = 0;
        foreach (var id in ids)
        {
            // ground-truth: the interface has no implementation in the corpus; its cards are not EF Core's
            var cards = await source.GetCardsAsync();
            total += cards.Count(c => c.Id == id);
        }

        return total;
    }

    public async Task<int> EntityConfiguredInAnExtensionMethod()
    {
        var total = 0;
        foreach (var voucher in await db.Set<Voucher>().ToListAsync())
        {
            total += voucher.Issuer.Name.Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> EntityConfiguredByType()
    {
        var total = 0;
        foreach (var receipt in await db.Set<Receipt>().ToListAsync())
        {
            total += receipt.Clerk.Name.Length; // expect: NP0002
        }

        return total;
    }
}
