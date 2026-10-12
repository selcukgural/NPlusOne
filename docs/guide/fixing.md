---
sidebar_position: 6
---

# Fixing N+1

The rewrites that remove almost every N+1, from the simplest to the most general.

## Include what you read

A lazy-loaded navigation read per element ([NP0002](../rules/NP0002.md)):

```csharp
// Before: one query per order for its customer
var orders = await db.Orders.ToListAsync();
var names = orders.Select(o => o.Customer.Name).ToList();

// After: one query with a join
var orders = await db.Orders.Include(o => o.Customer).ToListAsync();
```

The code fix adds the `Include` (and `ThenInclude` for deeper paths) to the query that produced the elements.

## Project what you need

Often better than `Include`: let the database return only the columns you use. No lazy loading, no tracking, no cycles for
the serializer ([NP0005](../rules/NP0005.md)):

```csharp
var rows = await db.Orders
    .Select(o => new OrderRow(o.Id, o.Customer.Name, o.Lines.Count))
    .ToListAsync();
```

`ProjectTo` (AutoMapper) and `ProjectToType` (Mapster) do the same from a mapping configuration.

## Load once, look up in memory

A query per element ([NP0001](../rules/NP0001.md)) with a key from the element:

```csharp
var ids = customers.Select(c => c.Id).ToList();
var ordersByCustomer = (await db.Orders.Where(o => ids.Contains(o.CustomerId)).ToListAsync())
    .ToLookup(o => o.CustomerId);

foreach (var customer in customers)
{
    var orders = ordersByCustomer[customer.Id];   // in memory
}
```

For a count or a sum per element, group in the database:

```csharp
var counts = await db.Orders
    .Where(o => ids.Contains(o.CustomerId))
    .GroupBy(o => o.CustomerId)
    .Select(g => new { g.Key, Count = g.Count() })
    .ToDictionaryAsync(x => x.Key, x => x.Count);
```

For an existence check (`Any`) per element, select the keys that exist into a `HashSet`.

## Give services a batch method

When the per-element call goes through a service or a repository (`GetByCustomerAsync(id)` in a loop), add the plural
method (`GetByCustomersAsync(ids)`) built like the lookup above, and call it once. The same goes for a MediatR request sent
per item: send one request for the whole list.

## Save once

`SaveChanges` per element ([NP0003](../rules/NP0003.md)) is one round trip and one transaction per element. Make all the
changes, then save once; EF Core batches the statements. For updates that do not need the entities in memory, one
set-based statement:

```csharp
await db.Orders.Where(o => ids.Contains(o.Id)).ExecuteUpdateAsync(s => s.SetProperty(o => o.Archived, true));
```

## When the loop is meant

Some loops make one round trip per iteration on purpose: a migration that processes rows in batches, a retry, a poll.
The analyzer recognises the common shapes; for the rest, suppress the warning with a comment that says why
(see [the analyzer](analyzer.md#what-it-does-not-report)).
