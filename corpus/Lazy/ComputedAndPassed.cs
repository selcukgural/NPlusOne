using Microsoft.EntityFrameworkCore;

namespace Corpus.Lazy;

// Navigations read inside the entity's own members, and inside helpers the elements are passed to.
public class Shipment
{
    public int Id { get; set; }
    public decimal Weight { get; set; }
    public virtual Customer Customer { get; set; }
    public virtual List<Parcel> Parcels { get; set; } = new();

    public decimal ParcelWeight => Parcels.Sum(p => p.Weight);
    public string CustomerName => Customer.Name;
    public bool IsHeavy => Weight > 100;

    public string Describe() => $"{Id} for {Customer.Name}";
}

public class Parcel
{
    public int Id { get; set; }
    public decimal Weight { get; set; }
}

public class ShipmentDb : DbContext
{
    public ShipmentDb(DbContextOptions<ShipmentDb> options) : base(options) { }
    public DbSet<Shipment> Shipments => Set<Shipment>();
}

public class ComputedAndPassed
{
    private readonly ShipmentDb _db;
    public ComputedAndPassed(ShipmentDb db) => _db = db;

    public async Task<decimal> ComputedPropertyInLambda()
    {
        var list = await _db.Shipments.ToListAsync();
        return list.Sum(s => s.ParcelWeight); // expect: NP0002
    }

    public async Task ComputedPropertyAndMethodInLoop()
    {
        var list = await _db.Shipments.ToListAsync();
        foreach (var s in list)
        {
            Console.WriteLine(s.CustomerName); // expect: NP0002
            Console.WriteLine(s.Describe());
            Console.WriteLine(s.IsHeavy);
        }
    }

    public async Task InstanceMethodInLoop()
    {
        var list = await _db.Shipments.ToListAsync();
        foreach (var s in list)
        {
            Console.WriteLine(s.Describe()); // expect: NP0002
        }
    }

    public async Task<decimal> ComputedPropertyIncluded()
    {
        var list = await _db.Shipments.Include(s => s.Parcels).ToListAsync();
        return list.Sum(s => s.ParcelWeight);
    }

    public async Task ListPassedToHelper()
    {
        var list = await _db.Shipments.ToListAsync();
        PrintCustomers(list); // expect: NP0002
    }

    public async Task ListPassedToHelperThatMaps()
    {
        var list = await _db.Shipments.ToListAsync();
        var names = MapAll(list); // expect: NP0002
    }

    public async Task IncludedListPassedToHelper()
    {
        var list = await _db.Shipments.Include(s => s.Customer).ToListAsync();
        PrintCustomers(list);
    }

    // Shipment.Customer has no foreign key property: EF Core cannot load it for an untracked entity and
    // throws instead, which is a bug but not one query per element.
    public async Task UntrackedListPassedToHelper()
    {
        var list = await _db.Shipments.AsNoTracking().ToListAsync();
        PrintCustomers(list);
    }

    public async Task<string> ListPassedToHelperThatReadsOneElement()
    {
        var list = await _db.Shipments.ToListAsync();
        return FirstName(list);
    }

    public async Task ListPassedToHelperReadingScalars()
    {
        var list = await _db.Shipments.ToListAsync();
        PrintWeights(list);
    }

    private static void PrintCustomers(List<Shipment> shipments)
    {
        // ground-truth: a helper given its elements; reported at the call that passes a query result
        foreach (var s in shipments)
        {
            Console.WriteLine(s.Customer.Name);
        }
    }

    private static List<string> MapAll(IEnumerable<Shipment> shipments) => shipments.Select(Name).ToList(); // ground-truth: a helper given its elements; reported at the call that passes a query result

    private static string Name(Shipment shipment) => shipment.Customer.Name;

    private static string FirstName(List<Shipment> shipments) => Name(shipments.First());

    private static void PrintWeights(List<Shipment> shipments)
    {
        foreach (var s in shipments)
        {
            Console.WriteLine(s.Weight);
        }
    }
}
