using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Endpoints;

// A hub method that streams (IAsyncEnumerable<T> or ChannelReader<T>) sends every item through the serializer.
public class StreamingHub(AppDb db) : Hub
{
    private static readonly Channel<Order> Queue = Channel.CreateUnbounded<Order>();

    public IAsyncEnumerable<Order> Orders() => db.Orders.AsAsyncEnumerable(); // expect: NP0005

    public async IAsyncEnumerable<Order> OrdersByIterator([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var order in db.Orders.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return order; // expect: NP0005
        }
    }

    public async IAsyncEnumerable<Order> OrdersFromList()
    {
        foreach (var order in await db.Orders.ToListAsync())
        {
            yield return order; // expect: NP0005
        }
    }

    public async IAsyncEnumerable<OrderDto> Rows([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var row in db.Orders.Select(o => new OrderDto(o.Id, o.Customer.Name)).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return row;
        }
    }

    public async IAsyncEnumerable<Customer> CustomersIncluded()
    {
        var customers = await db.Customers.Include(c => c.Address).Include(c => c.Orders).ThenInclude(o => o.Lines).ThenInclude(l => l.Product).ToListAsync();
        foreach (var customer in customers)
        {
            yield return customer;
        }
    }

    // Each streamed customer is one of many: its Address is loaded once per customer.
    public async IAsyncEnumerable<Customer> CustomersWithoutAddress()
    {
        var customers = await db.Customers.Include(c => c.Orders).ThenInclude(o => o.Lines).ThenInclude(l => l.Product).ToListAsync();
        foreach (var customer in customers)
        {
            yield return customer; // expect: NP0005
        }
    }

    // Written to a queue the hub does not return: nothing is serialized.
    public async Task Enqueue()
    {
        foreach (var order in await db.Orders.ToListAsync())
        {
            await Queue.Writer.WriteAsync(order);
        }
    }

    public ChannelReader<Order> OrdersByChannel()
    {
        var channel = Channel.CreateUnbounded<Order>();
        _ = Task.Run(async () =>
        {
            foreach (var order in await db.Orders.ToListAsync())
            {
                await channel.Writer.WriteAsync(order); // expect: NP0005
            }

            channel.Writer.Complete();
        });
        return channel.Reader;
    }

    public ChannelReader<OrderDto> RowsByChannel()
    {
        var channel = Channel.CreateUnbounded<OrderDto>();
        _ = Task.Run(async () =>
        {
            foreach (var row in await db.Orders.Select(o => new OrderDto(o.Id, o.Customer.Name)).ToListAsync())
            {
                channel.Writer.TryWrite(row);
            }

            channel.Writer.Complete();
        });
        return channel.Reader;
    }
}
