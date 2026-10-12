using Microsoft.EntityFrameworkCore;

namespace Corpus.Lazy;

// Code that reads navigations without naming them at the call site: ToString overrides (string formatting), records'
// generated members, Equals/GetHashCode overrides (Distinct, sets), conversion operators, property patterns, and
// walking up a lazy parent chain.
public class Floor
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int? ParentId { get; set; }
    public virtual Floor Parent { get; set; }
}

public class Desk
{
    public int Id { get; set; }
    public string Code { get; set; }
    public int? FloorId { get; set; }
    public virtual Floor Floor { get; set; }

    public override string ToString() => Code + "@" + Floor?.Name;
}

public class Agent
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public record Ticket
{
    public int Id { get; set; }
    public string Title { get; set; }
    public int? AgentId { get; set; }
    public virtual Agent Agent { get; set; }
}

public class Badge
{
    public int Id { get; set; }
    public int? DeskId { get; set; }
    public virtual Desk Desk { get; set; }

    public override bool Equals(object obj) => obj is Badge other && other.Desk?.Code == Desk?.Code;

    public override int GetHashCode() => Desk?.Code?.GetHashCode() ?? 0;
}

public class DeskLabel
{
    public string Text { get; set; }

    public static implicit operator DeskLabel(Desk desk) => new() { Text = desk.Floor?.Name };
}

public class ImplicitDb(DbContextOptions<ImplicitDb> options) : DbContext(options)
{
    public DbSet<Floor> Floors => Set<Floor>();
    public DbSet<Desk> Desks => Set<Desk>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Badge> Badges => Set<Badge>();
}

public class ImplicitReads(ImplicitDb db)
{
    public async Task<int> Interpolated()
    {
        var total = 0;
        foreach (var desk in await db.Desks.ToListAsync())
        {
            total += $"{desk}".Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> InterpolatedIncluded()
    {
        var total = 0;
        foreach (var desk in await db.Desks.Include(d => d.Floor).ToListAsync())
        {
            total += $"{desk}".Length;
        }

        return total;
    }

    // Floor does not override ToString: formatting it prints the type name.
    public async Task<int> FormattedWithoutOverride()
    {
        var total = 0;
        foreach (var floor in await db.Floors.ToListAsync())
        {
            total += $"{floor}".Length;
        }

        return total;
    }

    public async Task<int> Joined() => string.Join(", ", await db.Desks.ToListAsync()).Length; // expect: NP0002

    public async Task<int> Written()
    {
        var writer = new StringWriter();
        foreach (var desk in await db.Desks.ToListAsync())
        {
            writer.WriteLine(desk); // expect: NP0002
        }

        return writer.ToString().Length;
    }

    public async Task<int> ExplicitToString()
    {
        var total = 0;
        foreach (var desk in await db.Desks.ToListAsync())
        {
            total += desk.ToString().Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> RecordPrinted()
    {
        var total = 0;
        foreach (var ticket in await db.Tickets.ToListAsync())
        {
            total += ticket.ToString().Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> RecordInterpolated()
    {
        var total = 0;
        foreach (var ticket in await db.Tickets.ToListAsync())
        {
            total += $"{ticket}".Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> RecordsCompared() => (await db.Tickets.ToListAsync()).Distinct().Count();

    public async Task<int> EqualityDistinct() => (await db.Badges.ToListAsync()).Distinct().Count(); // expect: NP0002

    public async Task<int> EqualityHashSet() => new HashSet<Badge>(await db.Badges.ToListAsync()).Count; // expect: NP0002

    public async Task<int> EqualityIncluded() => (await db.Badges.Include(b => b.Desk).ToListAsync()).Distinct().Count();

    public async Task<int> Converted()
    {
        var total = 0;
        foreach (var desk in await db.Desks.ToListAsync())
        {
            DeskLabel label = desk; // expect: NP0002
            total += label.Text?.Length ?? 0;
        }

        return total;
    }

    public async Task<int> PropertyPattern() => (await db.Desks.ToListAsync()).Count(d => d is { Floor.Name: "Floor-1" }); // expect: NP0002

    public async Task<int> NestedPropertyPattern() => (await db.Desks.ToListAsync()).Count(d => d is { Floor: { Name: "Floor-1" } }); // expect: NP0002

    // Desk is included; the inner pattern reads Desk.Floor, which is not.
    public async Task<int> DeepPropertyPattern()
        => (await db.Badges.Include(b => b.Desk).ToListAsync()).Count(b => b is { Desk: { Floor: { Name: "Floor-1" } } }); // expect: NP0002

    public async Task<int> PatternOnScalars() => (await db.Desks.ToListAsync()).Count(d => d is { Code: "Desk-1", FloorId: 1 });

    // Reads the same parent every time: it is loaded once.
    public async Task<int> ParentReadInLoop()
    {
        var floor = await db.Floors.OrderByDescending(f => f.Id).FirstAsync();
        Floor parent = null;
        var steps = 0;
        while (steps < 3 && floor.Parent != null)
        {
            parent = floor.Parent;
            steps++;
        }

        return parent?.Id ?? 0;
    }

    public async Task<int> ParentChainOfOne()
    {
        var floor = await db.Floors.OrderByDescending(f => f.Id).FirstAsync();
        var depth = 0;
        // ground-truth: the seeder builds two-level trees, so the walk is one step whatever the row count
        while (floor.Parent != null) // expect: NP0002
        {
            floor = floor.Parent;
            depth++;
        }

        return depth;
    }
}
