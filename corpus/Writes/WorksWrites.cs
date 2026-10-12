using Microsoft.EntityFrameworkCore;

namespace Corpus.Writes;

// The catalogue's "works" rows on writes: tracking changes per element sends nothing until one SaveChanges, and a unit
// of work that saves is a SaveChanges per element.
public interface IUnitOfWork
{
    Task CommitAsync();
}

public class UnitOfWork(AppDb db) : IUnitOfWork
{
    public Task CommitAsync() => db.SaveChangesAsync();
}

public class WorksWrites(AppDb db, IUnitOfWork unitOfWork)
{
    public async Task TrackedThenSavedOnce()
    {
        foreach (var customer in await db.Customers.ToListAsync())
        {
            db.Orders.Add(new Order { CustomerId = customer.Id, Total = 1 });
            db.Update(customer);
            db.Attach(customer);
        }

        await db.SaveChangesAsync();
    }

    public async Task UnitOfWorkPerElement()
    {
        foreach (var order in await db.Orders.ToListAsync())
        {
            order.Total += 1;
            await unitOfWork.CommitAsync(); // expect: NP0003
        }
    }
}
