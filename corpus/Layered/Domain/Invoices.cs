namespace Corpus.Layered.Domain;

public class Invoice
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
}

// Implemented in an infrastructure project neither layer references.
public interface IInvoiceRepository
{
    Task<Invoice> GetByIdAsync(int id);

    Task<List<Invoice>> GetByIdsAsync(List<int> ids);

    Task AddAsync(Invoice invoice);
}

// A domain service in the project that declares the repository.
public class InvoiceManager(IInvoiceRepository invoices)
{
    public async Task<bool> AllPositive(List<int> ids)
    {
        foreach (var id in ids)
        {
            // ground-truth: the repository is implemented in a project this layer does not reference
            if ((await invoices.GetByIdAsync(id)).Amount <= 0) // expect: NP0006
            {
                return false;
            }
        }

        return true;
    }
}
