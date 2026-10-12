using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

public record GetCustomerTotal(int CustomerId) : IRequest<decimal>;

public class GetCustomerTotalHandler(AppDb db) : IRequestHandler<GetCustomerTotal, decimal>
{
    public Task<decimal> Handle(GetCustomerTotal request, CancellationToken cancellationToken)
        => db.Orders.Where(o => o.CustomerId == request.CustomerId).SumAsync(o => o.Total, cancellationToken);
}

public record Square(int Value) : IRequest<int>;

public class SquareHandler : IRequestHandler<Square, int>
{
    public Task<int> Handle(Square request, CancellationToken cancellationToken) => Task.FromResult(request.Value * request.Value);
}

public record OrderShipped(int OrderId) : INotification;

public class OrderShippedHandler(AppDb db) : INotificationHandler<OrderShipped>
{
    public async Task Handle(OrderShipped notification, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FindAsync([notification.OrderId], cancellationToken);
    }
}

public class MediatorCalls(ISender sender, IMediator mediator, IPublisher publisher)
{
    public async Task SendPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            var total = await sender.Send(new GetCustomerTotal(id)); // expect: NP0001
        }
    }

    public async Task SendThroughMediatorPerElement(List<int> ids)
    {
        var totals = await Task.WhenAll(ids.Select(id => mediator.Send(new GetCustomerTotal(id)))); // expect: NP0001
    }

    public async Task PublishPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            await publisher.Publish(new OrderShipped(id)); // expect: NP0001
        }
    }

    public async Task HelperThatSendsPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            await TotalFor(id); // expect: NP0001
        }
    }

    private Task<decimal> TotalFor(int id) => sender.Send(new GetCustomerTotal(id));

    public async Task<decimal> SendOnce(int id) => await sender.Send(new GetCustomerTotal(id));

    public async Task SendWithoutQueryPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            var square = await sender.Send(new Square(id));
        }
    }
}
