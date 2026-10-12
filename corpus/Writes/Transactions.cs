using Microsoft.EntityFrameworkCore;

namespace Corpus.Writes;

// A transaction started per element is a begin and a commit per element, and each element commits on its own.
public class Transactions(AppDb db)
{
    public async Task TransactionPerElement()
    {
        foreach (var order in await db.Orders.ToListAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(); // expect: NP0003
            order.Total += 1;
            await transaction.CommitAsync();
        }

        await db.SaveChangesAsync();
    }

    public void TransactionPerElementSync()
    {
        foreach (var order in db.Orders.ToList())
        {
            using var transaction = db.Database.BeginTransaction(System.Data.IsolationLevel.ReadCommitted); // expect: NP0003
            order.Total += 1;
            transaction.Commit();
        }
    }

    public async Task OneTransaction()
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var order in await db.Orders.ToListAsync())
        {
            order.Total += 1;
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
