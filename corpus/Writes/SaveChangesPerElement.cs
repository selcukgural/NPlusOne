using Microsoft.EntityFrameworkCore;

namespace Corpus.Writes;

// SaveChanges once per element: one round trip (and one transaction) per row.
public class SaveChangesPerElement
{
    private readonly AppDb _db;
    public SaveChangesPerElement(AppDb db) => _db = db;

    public async Task SaveInForeach(List<Order> orders)
    {
        foreach (var o in orders)
        {
            o.Total = 0;
            await _db.SaveChangesAsync(); // expect: NP0003
        }
    }

    public void SaveInForEachLambda(List<Product> products)
    {
        // ground-truth-arguments: new
        products.ForEach(p =>
        {
            _db.Products.Add(p);
            _db.SaveChanges(); // expect: NP0003
        });
    }

    public async Task SaveThroughHelper(List<Order> orders)
    {
        foreach (var o in orders)
        {
            await Persist(o); // expect: NP0003
        }
    }

    private async Task Persist(Order order)
    {
        _db.Update(order);
        await _db.SaveChangesAsync();
    }

    public async Task SaveOnceAfterTheLoop(List<Order> orders)
    {
        foreach (var o in orders)
        {
            o.Total = 0;
        }

        await _db.SaveChangesAsync();
    }

    public async Task SavePerBatch(List<Product> products)
    {
        // ground-truth-arguments: new
        foreach (var batch in products.Chunk(1000))
        {
            _db.Products.AddRange(batch);
            await _db.SaveChangesAsync();
        }
    }
}
