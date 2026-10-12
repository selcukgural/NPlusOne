using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

// MediatR shapes beyond IRequest<TResponse>: requests without a response, handlers that inherit
// Handle from a base class, and Send(object).
public record ArchiveOrder(int OrderId) : IRequest;

public class ArchiveOrderHandler(AppDb db) : IRequestHandler<ArchiveOrder>
{
    public async Task Handle(ArchiveOrder request, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstAsync(o => o.Id == request.OrderId, cancellationToken);
    }
}

public record Ping(int Value) : IRequest;

public class PingHandler : IRequestHandler<Ping>
{
    public Task Handle(Ping request, CancellationToken cancellationToken) => Task.CompletedTask;
}

public abstract class QueryHandler<TRequest, TResponse> : IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public abstract Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

public record CountOrders(int CustomerId) : IRequest<int>;

public class CountOrdersHandler(AppDb db) : QueryHandler<CountOrders, int>
{
    public override Task<int> Handle(CountOrders request, CancellationToken cancellationToken)
        => db.Orders.CountAsync(o => o.CustomerId == request.CustomerId, cancellationToken);
}

public class MediatRVariants(ISender sender)
{
    public async Task SendWithoutResponsePerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            await sender.Send(new ArchiveOrder(id)); // expect: NP0001
        }
    }

    public async Task SendWithoutResponseOrQueryPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            await sender.Send(new Ping(id));
        }
    }

    public async Task SendToInheritedHandlerPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            var count = await sender.Send(new CountOrders(id)); // expect: NP0001
        }
    }

    public async Task SendAsObjectPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            object request = new CountOrders(id);
            var count = await sender.Send(request); // expect: NP0001
        }
    }

    public async Task SendAsObjectWithoutQueryPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            object request = new Ping(id);
            await sender.Send(request);
        }
    }
}
