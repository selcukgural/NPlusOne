using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Reads;

// MediatR runs every registered pipeline behavior whose constraints accept the request around its handler,
// on every Send; stream requests go to an IStreamRequestHandler when the stream is enumerated.
public interface IAudited;

public interface ITraced;

public record AuditedCount(int CustomerId) : IRequest<int>, IAudited;

public record TracedCount(int CustomerId) : IRequest<int>, ITraced;

public record PlainCount(int CustomerId) : IRequest<int>;

public class AuditedCountHandler : IRequestHandler<AuditedCount, int>
{
    public Task<int> Handle(AuditedCount request, CancellationToken cancellationToken) => Task.FromResult(request.CustomerId);
}

public class TracedCountHandler : IRequestHandler<TracedCount, int>
{
    public Task<int> Handle(TracedCount request, CancellationToken cancellationToken) => Task.FromResult(request.CustomerId);
}

public class PlainCountHandler : IRequestHandler<PlainCount, int>
{
    public Task<int> Handle(PlainCount request, CancellationToken cancellationToken) => Task.FromResult(request.CustomerId);
}

// Registered below: runs for every IAudited request.
public class AuditBehavior<TRequest, TResponse>(AppDb db) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IAudited
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        await db.Customers.CountAsync(cancellationToken);
        return await next();
    }
}

// Never registered: MediatR does not run it.
public class TraceBehavior<TRequest, TResponse>(AppDb db) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ITraced
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        await db.Customers.CountAsync(cancellationToken);
        return await next();
    }
}

public record CountedLines(int OrderId) : IRequest<int>;

public class CountedLinesHandler : IRequestHandler<CountedLines, int>
{
    public Task<int> Handle(CountedLines request, CancellationToken cancellationToken) => Task.FromResult(request.OrderId);
}

// A behavior for one request type, registered by its closed interface.
public class CountedLinesBehavior(AppDb db) : IPipelineBehavior<CountedLines, int>
{
    public async Task<int> Handle(CountedLines request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => await db.OrderLines.CountAsync(l => l.OrderId == request.OrderId, cancellationToken) + await next();
}

public static class MediatRSetup
{
    public static void Configure(MediatRServiceConfiguration configuration)
    {
        configuration.AddOpenBehavior(typeof(AuditBehavior<,>));
        configuration.AddBehavior<IPipelineBehavior<CountedLines, int>, CountedLinesBehavior>();
    }
}

public record StreamOrders(int CustomerId) : IStreamRequest<Order>;

public class StreamOrdersHandler(AppDb db) : IStreamRequestHandler<StreamOrders, Order>
{
    public async IAsyncEnumerable<Order> Handle(StreamOrders request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var order in db.Orders.Where(o => o.CustomerId == request.CustomerId).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return order;
        }
    }
}

public record StreamTotals(int CustomerId) : IStreamRequest<decimal>;

public class StreamTotalsHandler : IStreamRequestHandler<StreamTotals, decimal>
{
    public async IAsyncEnumerable<decimal> Handle(StreamTotals request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return request.CustomerId;
    }
}

public class MediatRPipeline(ISender sender, AppDb db)
{
    public async Task BehaviorQueriesPerElement()
    {
        foreach (var customer in await db.Customers.ToListAsync())
        {
            await sender.Send(new AuditedCount(customer.Id)); // expect: NP0001
        }
    }

    public async Task ClosedBehaviorQueriesPerElement()
    {
        foreach (var order in await db.Orders.ToListAsync())
        {
            await sender.Send(new CountedLines(order.Id)); // expect: NP0001
        }
    }

    public async Task UnregisteredBehavior()
    {
        foreach (var customer in await db.Customers.ToListAsync())
        {
            await sender.Send(new TracedCount(customer.Id));
        }
    }

    public async Task NoBehaviorApplies()
    {
        foreach (var customer in await db.Customers.ToListAsync())
        {
            await sender.Send(new PlainCount(customer.Id));
        }
    }

    public async Task<decimal> StreamPerElement()
    {
        var total = 0m;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            await foreach (var order in sender.CreateStream(new StreamOrders(customer.Id))) // expect: NP0001
            {
                total += order.Total;
            }
        }

        return total;
    }

    public async Task<decimal> StreamWithoutQueriesPerElement()
    {
        var total = 0m;
        foreach (var customer in await db.Customers.ToListAsync())
        {
            await foreach (var value in sender.CreateStream(new StreamTotals(customer.Id)))
            {
                total += value;
            }
        }

        return total;
    }

    public async Task<decimal> StreamOnce()
    {
        var total = 0m;
        await foreach (var order in sender.CreateStream(new StreamOrders(1)))
        {
            total += order.Total;
        }

        return total;
    }
}
