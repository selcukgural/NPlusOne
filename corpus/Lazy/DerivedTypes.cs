using Microsoft.EntityFrameworkCore;

namespace Corpus.Lazy;

// Only the base type has a DbSet; the derived types are mapped in OnModelCreating (TPH), and their own navigations
// load lazily like any other.
public abstract class Party
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public class Company : Party
{
    public int? SectorId { get; set; }
    public virtual Sector Sector { get; set; }
}

public class Sector
{
    public int Id { get; set; }
    public string Label { get; set; }
}

public class PartyDb(DbContextOptions<PartyDb> options) : DbContext(options)
{
    public DbSet<Party> Parties => Set<Party>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Company>();
}

public class DerivedTypes(PartyDb db)
{
    public async Task<int> DerivedNavigationPerElement()
    {
        var total = 0;
        foreach (var company in await db.Parties.OfType<Company>().ToListAsync())
        {
            total += company.Sector.Label.Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> DerivedNavigationIncluded()
    {
        var total = 0;
        foreach (var company in await db.Parties.OfType<Company>().Include(c => c.Sector).ToListAsync())
        {
            total += company.Sector.Label.Length;
        }

        return total;
    }

    public async Task<int> DerivedScalarsPerElement()
    {
        var total = 0;
        foreach (var company in await db.Parties.OfType<Company>().ToListAsync())
        {
            total += company.Name.Length + (company.SectorId ?? 0);
        }

        return total;
    }
}
