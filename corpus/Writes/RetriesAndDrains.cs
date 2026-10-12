using Microsoft.EntityFrameworkCore;

namespace Corpus.Writes;

// Loops that repeat a round trip on purpose: a timer that polls, a save retried on a concurrency conflict (the EF Core
// documentation's own sample), and a table drained in pages. And the per-element loops next to them that stay N+1.
public class RetriesAndDrains(AppDb db)
{
    // Two ticks: the runtime guard counts repeats of one query and cannot tell a poll from an N+1, so a third tick would
    // be reported there (a poll belongs in a background service, not in a guarded request or test).
    public async Task<int> PollWithPeriodicTimer()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1));
        var ticks = 0;
        var total = 0;
        while (ticks++ < 2 && await timer.WaitForNextTickAsync())
        {
            total += await db.Orders.CountAsync(o => o.Total > 0);
        }

        return total;
    }

    public void SaveRetriedOnConflict()
    {
        var order = db.Orders.First();
        order.Total += 1;
        var saved = false;
        while (!saved)
        {
            try
            {
                db.SaveChanges();
                saved = true;
            }
            catch (DbUpdateConcurrencyException conflict)
            {
                foreach (var entry in conflict.Entries)
                {
                    entry.OriginalValues.SetValues(entry.GetDatabaseValues()!);
                }
            }
        }
    }

    public async Task SaveRetriedThreeTimes()
    {
        var order = await db.Orders.FirstAsync();
        order.Total += 1;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                await Task.Yield();
            }
        }
    }

    // LINQ's Take in the body reads no queue: the loop still walks no collection, it retries.
    public void SaveRetriedWithLinqTake()
    {
        var amounts = new[] { 1m, 2m, 3m, 4m };
        var order = db.Orders.First();
        var saved = false;
        while (!saved)
        {
            order.Total = amounts.Take(3).Sum();
            try
            {
                db.SaveChanges();
                saved = true;
            }
            catch (DbUpdateConcurrencyException)
            {
            }
        }
    }

    public async Task DrainInPages()
    {
        while (true)
        {
            var batch = await db.OrderLines.OrderBy(l => l.Id).Where(l => l.Price < 0).Take(100).ToListAsync();
            if (batch.Count == 0)
            {
                break;
            }

            db.OrderLines.RemoveRange(batch);
            await db.SaveChangesAsync();
        }
    }

    // A try around each element's save is error handling, not a retry: still one save per element.
    public async Task SavePerElementWithErrorHandling()
    {
        var orders = await db.Orders.ToListAsync();
        for (var i = 0; i < orders.Count; i++)
        {
            orders[i].Total += 1;
            try
            {
                await db.SaveChangesAsync(); // expect: NP0003
            }
            catch (DbUpdateException)
            {
            }
        }
    }

    // Paged reads, then a save per element of the page: the inner loop is per element.
    public async Task SavePerElementOfAPage()
    {
        var page = await db.Orders.OrderBy(o => o.Id).Take(100).ToListAsync();
        foreach (var order in page)
        {
            order.Total += 1;
            await db.SaveChangesAsync(); // expect: NP0003
        }
    }
}
