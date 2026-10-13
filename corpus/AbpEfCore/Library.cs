using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.DependencyInjection;
using Volo.Abp.EntityFrameworkCore.Modeling;
using Volo.Abp.Modularity;

namespace Corpus.AbpEfCore;

// The EF Core layer of an ABP application: its DbContext maps the entities in OnModelCreating (DbSets only so that ABP
// registers the default repositories), a custom repository derives from ABP's EfCoreRepository, and the module
// configures the details ABP loads with includeDetails: true.
public class Shelf : BasicAggregateRoot<int>
{
    public string Name { get; set; }
    public virtual ICollection<ShelfItem> Items { get; set; }
}

public class ShelfItem : Entity<int>
{
    public string Title { get; set; }
    public int ShelfId { get; set; }
    public virtual Shelf Shelf { get; set; }
    public int LabelId { get; set; }
    public virtual Label Label { get; set; }
}

public class Label : Entity<int>
{
    public string Text { get; set; }
}

public class LibraryDbContext(DbContextOptions<LibraryDbContext> options) : AbpDbContext<LibraryDbContext>(options)
{
    public DbSet<Shelf> Shelves { get; set; }
    public DbSet<ShelfItem> ShelfItems { get; set; }
    public DbSet<Label> Labels { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Shelf>(b => b.ConfigureByConvention());
        builder.Entity<ShelfItem>(b =>
        {
            b.ConfigureByConvention();
            b.HasOne(i => i.Shelf).WithMany(s => s.Items).HasForeignKey(i => i.ShelfId);
        });
    }
}

[DependsOn(typeof(AbpEntityFrameworkCoreModule))]
public class LibraryEntityFrameworkCoreModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<LibraryDbContext>(options =>
        {
            options.AddDefaultRepositories(includeAllEntities: true);
            options.AddRepository<Shelf, EfCoreShelfRepository>();
        });
        Configure<AbpEntityOptions>(options => options.Entity<ShelfItem>(item => item.DefaultWithDetailsFunc = query => query.Include(i => i.Label)));
    }
}

public interface IShelfRepository : IRepository<Shelf, int>
{
    Task<int> CountPerElement(List<int> ids);
}

public class EfCoreShelfRepository(IDbContextProvider<LibraryDbContext> dbContextProvider)
    : EfCoreRepository<LibraryDbContext, Shelf, int>(dbContextProvider), IShelfRepository
{
    public async Task<int> CountPerElement(List<int> ids)
    {
        var total = 0;
        var dbSet = await GetDbSetAsync();
        foreach (var id in ids)
        {
            total += await dbSet.CountAsync(s => s.Id == id); // expect: NP0001
        }

        return total;
    }

    public async Task<int> BaseGetPerElement(List<int> ids)
    {
        var total = 0;
        foreach (var id in ids)
        {
            total += (await GetAsync(id, includeDetails: false)).Name.Length; // expect: NP0001
        }

        return total;
    }

    public async Task<int> ContextFindPerElement(List<int> ids)
    {
        var total = 0;
        var context = await GetDbContextAsync();
        foreach (var id in ids)
        {
            total += (await context.Set<Shelf>().FindAsync(id))!.Name.Length; // expect: NP0001
        }

        return total;
    }

    // The repository overrides an ABP method with a body of its own (a cached count): the body is followed, not ABP's.
    private long? _count;

    public override async Task<long> GetCountAsync(CancellationToken cancellationToken = default)
        => _count ??= await base.GetCountAsync(cancellationToken);

    public async Task<long> CachedCountPerElement(List<int> ids)
    {
        long total = 0;
        foreach (var id in ids)
        {
            total += await GetCountAsync() + id;
        }

        return total;
    }

    public async Task<int> NamesLoadedOnce(List<int> ids)
    {
        var total = 0;
        var dbSet = await GetDbSetAsync();
        var shelves = await dbSet.Where(s => ids.Contains(s.Id)).ToListAsync();
        foreach (var id in ids)
        {
            total += shelves.Single(s => s.Id == id).Name.Length;
        }

        return total;
    }
}

public class ShelfQueries(IRepository<ShelfItem, int> items, IShelfRepository shelves)
{
    public async Task<int> LabelsWithoutDetails()
    {
        var total = 0;
        foreach (var item in await items.GetListAsync())
        {
            total += item.Label.Text.Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> LabelsWithConfiguredDetails()
    {
        var total = 0;
        foreach (var item in await items.GetListAsync(includeDetails: true))
        {
            total += item.Label.Text.Length;
        }

        return total;
    }

    public async Task<int> LabelsOfConfiguredWithDetails()
    {
        var total = 0;
        foreach (var item in await (await items.WithDetailsAsync()).ToListAsync())
        {
            total += item.Label.Text.Length;
        }

        return total;
    }

    public async Task<int> CustomRepositoryPerElement(List<int> ids)
    {
        var total = 0;
        foreach (var id in ids)
        {
            total += await shelves.CountPerElement([id]); // expect: NP0001
        }

        return total;
    }

    public async Task RenamePerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            var shelf = await shelves.GetAsync(id, includeDetails: false); // expect: NP0001
            shelf.Name += "!";
            await shelves.UpdateAsync(shelf, autoSave: true); // expect: NP0003
        }
    }

    public async Task RenameSavedOnce(List<int> ids)
    {
        var all = await shelves.GetListAsync(s => ids.Contains(s.Id));
        foreach (var shelf in all)
        {
            shelf.Name += "!";
            await shelves.UpdateAsync(shelf);
        }
    }
}
