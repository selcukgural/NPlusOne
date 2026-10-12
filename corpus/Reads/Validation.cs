using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

// FluentValidation runs the rules chained on RuleForEach once per item of the collection.
public record PlaceOrder(int CustomerId, List<int> ProductIds);

public class PlaceOrderValidator : AbstractValidator<PlaceOrder>
{
    public PlaceOrderValidator(AppDb db)
    {
        RuleForEach(x => x.ProductIds)
            .MustAsync(async (id, cancellationToken) => await db.Products.AnyAsync(p => p.Id == id, cancellationToken)); // expect: NP0001
        RuleFor(x => x.CustomerId)
            .MustAsync(async (id, cancellationToken) => await db.Customers.AnyAsync(c => c.Id == id, cancellationToken));
        RuleForEach(x => x.ProductIds).Must(id => id > 0);
    }
}

public record RenameCustomer(int CustomerId, string Name);

public class RenameCustomerValidator : AbstractValidator<RenameCustomer>
{
    public RenameCustomerValidator(AppDb db)
    {
        RuleFor(x => x.CustomerId)
            .MustAsync(async (id, cancellationToken) => await db.Customers.AnyAsync(c => c.Id == id, cancellationToken))
            .WithMessage("Unknown customer");
    }
}

public class Validation(AppDb db, PlaceOrderValidator placeOrder, RenameCustomerValidator rename)
{
    public async Task<bool> ValidateOrder()
    {
        var ids = await db.Products.Select(p => p.Id).ToListAsync();
        var result = await placeOrder.ValidateAsync(new PlaceOrder(1, ids)); // ground-truth-proves: NP0001 (the RuleForEach rule in PlaceOrderValidator)
        return result.IsValid;
    }

    public async Task<bool> ValidateRename()
    {
        var result = await rename.ValidateAsync(new RenameCustomer(1, "x"));
        return result.IsValid;
    }
}
