using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

// Queries reached through calls made once per element.
public class CallChains
{
    private readonly AppDb _db;
    private readonly IOrderRepository _orders;
    private readonly ICustomerRepository _customers;
    private readonly OrderService _service;

    public CallChains(AppDb db, IOrderRepository orders, ICustomerRepository customers, OrderService service)
    {
        _db = db;
        _orders = orders;
        _customers = customers;
        _service = service;
    }

    public async Task PrivateHelper(List<int> ids)
    {
        foreach (var id in ids)
        {
            await LoadOrders(id); // expect: NP0001
        }
    }

    private Task<List<Order>> LoadOrders(int customerId) => _db.Orders.Where(o => o.CustomerId == customerId).ToListAsync();

    public async Task HelperTwoLevelsDown(List<int> ids)
    {
        foreach (var id in ids)
        {
            await Outer(id); // expect: NP0001
        }
    }

    private Task<int> Outer(int id) => Inner(id);
    private Task<int> Inner(int id) => _db.Orders.CountAsync(o => o.CustomerId == id);

    public async Task InterfaceWithVisibleImplementation(List<int> ids)
    {
        foreach (var id in ids)
        {
            var orders = await _orders.GetByCustomerAsync(id); // expect: NP0001
        }
    }

    public async Task PublicServiceMethod(List<int> ids)
    {
        foreach (var id in ids)
        {
            var total = await _service.TotalFor(id); // expect: NP0001
        }
    }

    public async Task InterfaceWithoutImplementation(List<int> ids)
    {
        foreach (var id in ids)
        {
            var c = await _customers.GetByIdAsync(id); // expect: NP0006
            var n = await _customers.CountOrdersAsync(id); // expect: NP0006
        }
    }

    public void InterfaceVoidMethodIsNotDataAccess(List<Customer> customers)
    {
        foreach (var c in customers)
        {
            _customers.Remember(c);
        }
    }

    public async Task QueryableParameterGetsAnEfQuery(List<int> ids)
    {
        foreach (var id in ids)
        {
            await CountIn(_db.Orders, id); // expect: NP0001
        }
    }

    public async Task QueryableParameterGetsAnInMemoryQuery(List<int> ids, List<Order> cached)
    {
        foreach (var id in ids)
        {
            await Task.Yield();
            CountInMemory(cached.AsQueryable(), id);
        }
    }

    private static Task<int> CountIn(IQueryable<Order> source, int id) => source.CountAsync(o => o.CustomerId == id);

    private static int CountInMemory(IQueryable<Order> source, int id) => source.Count(o => o.CustomerId == id);

    public List<int> MethodGroupThatQueries(List<int> ids)
        => ids.Select(CountSync).ToList(); // expect: NP0001

    private int CountSync(int id) => _db.Orders.Count(o => o.CustomerId == id);

    public async Task VirtualOverride(List<int> ids, BaseLoader loader)
    {
        foreach (var id in ids)
        {
            await loader.Load(id); // expect: NP0001
        }
    }

    public async Task RecursiveWithLoop(int? parentId)
    {
        var children = await _db.Categories.Where(c => c.ParentId == parentId).ToListAsync();
        foreach (var child in children)
        {
            await RecursiveWithLoop(child.Id); // expect: NP0001
        }
    }

    public async Task<int> RecursiveWithoutLoop(int categoryId)
    {
        var category = await _db.Categories.FirstAsync(c => c.Id == categoryId);
        return category.ParentId is null ? 0 : 1 + await RecursiveWithoutLoop(category.ParentId.Value); // expect: NP0004
    }

    public async Task UserIteratingHelper(List<int> ids)
    {
        await EachAsync(ids, async id => await _db.Orders.CountAsync(o => o.CustomerId == id)); // expect: NP0001
    }

    private static async Task EachAsync<T>(IEnumerable<T> items, Func<T, Task<int>> action)
    {
        foreach (var item in items)
        {
            await action(item);
        }
    }
}

public class OrderService
{
    private readonly AppDb _db;
    public OrderService(AppDb db) => _db = db;
    public Task<decimal> TotalFor(int customerId) => _db.Orders.Where(o => o.CustomerId == customerId).SumAsync(o => o.Total);
}

public abstract class BaseLoader
{
    public abstract Task Load(int id);
}

public class DbLoader : BaseLoader
{
    private readonly AppDb _db;
    public DbLoader(AppDb db) => _db = db;
    public override Task Load(int id) => _db.Orders.Where(o => o.Id == id).LoadAsync();
}

// An override that queries and then calls the base implementation (ABP application services override CrudAppService's
// GetAsync this way): base.NameLength runs the base body once, it never comes back to the override, so it is no recursion.
public class CategoryReader(AppDb db)
{
    public virtual async Task<int> NameLength(int id) => (await db.Categories.FirstAsync(c => c.Id == id)).Name.Length;
}

public class AuditedCategoryReader(AppDb db) : CategoryReader(db)
{
    public override async Task<int> NameLength(int id)
    {
        await db.Categories.CountAsync(c => c.Id == id);
        return await base.NameLength(id);
    }
}
