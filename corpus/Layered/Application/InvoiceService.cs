using Corpus.Layered.Domain;

namespace Corpus.Layered.Application;

// The application layer names no data access type of its own; the repository comes from the domain project.
public class InvoiceService(IInvoiceRepository invoices)
{
    public async Task<decimal> TotalPerElement(List<int> ids)
    {
        var total = 0m;
        foreach (var id in ids)
        {
            // ground-truth: the repository is implemented in a project this layer does not reference
            total += (await invoices.GetByIdAsync(id)).Amount; // expect: NP0006
        }

        return total;
    }

    public async Task<decimal> TotalLoadedOnce(List<int> ids)
    {
        // ground-truth: the repository is implemented in a project this layer does not reference
        var all = await invoices.GetByIdsAsync(ids);
        return all.Sum(i => i.Amount);
    }

    public async Task AddPerElement(List<Invoice> newInvoices)
    {
        foreach (var invoice in newInvoices)
        {
            // ground-truth: the repository is implemented in a project this layer does not reference
            await invoices.AddAsync(invoice);
        }
    }
}
