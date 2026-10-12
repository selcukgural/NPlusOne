using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads.SourceGeneratedMediator;

// The source-generated Mediator library: commands, queries and requests with ValueTask handlers.
public record GetOrderCount(int CustomerId) : IQuery<int>;

public class GetOrderCountHandler(AppDb db) : IQueryHandler<GetOrderCount, int>
{
    public async ValueTask<int> Handle(GetOrderCount query, CancellationToken cancellationToken)
        => await db.Orders.CountAsync(o => o.CustomerId == query.CustomerId, cancellationToken);
}

public record CloseOrder(int OrderId) : ICommand;

public class CloseOrderHandler(AppDb db) : ICommandHandler<CloseOrder>
{
    public async ValueTask<Unit> Handle(CloseOrder command, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstAsync(o => o.Id == command.OrderId, cancellationToken);
        return Unit.Value;
    }
}

public record Double(int Value) : IRequest<int>;

public class DoubleHandler : IRequestHandler<Double, int>
{
    public ValueTask<int> Handle(Double request, CancellationToken cancellationToken) => ValueTask.FromResult(request.Value * 2);
}

public record CustomerRenamed(int CustomerId) : INotification;

public class CustomerRenamedHandler(AppDb db) : INotificationHandler<CustomerRenamed>
{
    public async ValueTask Handle(CustomerRenamed notification, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FirstAsync(c => c.Id == notification.CustomerId, cancellationToken);
    }
}

public class MediatorCalls(IMediator mediator)
{
    public async Task QueryPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            var count = await mediator.Send(new GetOrderCount(id)); // expect: NP0001
        }
    }

    public async Task CommandPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            await mediator.Send(new CloseOrder(id)); // expect: NP0001
        }
    }

    public async Task PublishPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            await mediator.Publish(new CustomerRenamed(id)); // expect: NP0001
        }
    }

    public async Task RequestWithoutQueryPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            var doubled = await mediator.Send(new Double(id));
        }
    }

    public async Task<int> QueryOnce(int id) => await mediator.Send(new GetOrderCount(id));
}
