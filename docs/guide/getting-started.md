---
sidebar_position: 2
---

# Getting started

Five minutes from nothing to a failing build and a failing test for an N+1 query. You need the .NET 8 SDK or later and
a project that uses EF Core 8, 9 or 10. [Packages](packages.md) lists the three packages and which project each goes in.

## 1. Add the analyzer

In the project that holds your EF Core code (and in every project that calls it):

```bash
dotnet add package NPlusOne.Analyzers
```

The package is a development dependency: it runs in the compiler and adds nothing to your application's output.

## 2. Build and read the warnings

```bash
dotnet build
```

Any N+1 the analyzer can prove shows up as a warning, in the build output and in the IDE:

```text
OrderService.cs(42,31): warning NP0001: 'IOrderRepository.GetByCustomerAsync' runs a query ('ToListAsync' in
  OrderRepository.GetByCustomerAsync) once per element of the foreach loop over 'customers'. Load the rows for all
  elements up front with one query, Where(o => keys.Contains(o.CustomerId)), and group them with ToLookup(o => o.CustomerId).
```

Each message names the round trip, the loop that repeats it, the calls in between, and the rewrite. The rule id links to
its page ([NP0001](../rules/NP0001.md) here) with examples and fixes.

## 3. Fix it

For the warning above, load all orders once and look them up in memory:

```csharp
var customers = await db.Customers.ToListAsync();
var ids = customers.Select(c => c.Id).ToList();
var ordersByCustomer = (await db.Orders.Where(o => ids.Contains(o.CustomerId)).ToListAsync())
    .ToLookup(o => o.CustomerId);

foreach (var customer in customers)
{
    var orders = ordersByCustomer[customer.Id];
}
```

Two rules come with a code fix (the light bulb in the IDE): [NP0002](../rules/NP0002.md) adds the missing `Include`,
[NP0003](../rules/NP0003.md) moves `SaveChanges` after the loop. [Fixing N+1](fixing.md) lists the usual rewrites.

## 4. Fail the build on N+1

Once the existing warnings are fixed (or suppressed where they are intended), keep them from coming back. In
`.editorconfig` at the solution root:

```ini
[*.cs]
dotnet_diagnostic.NP0001.severity = error
dotnet_diagnostic.NP0002.severity = error
dotnet_diagnostic.NP0003.severity = error
dotnet_diagnostic.NP0005.severity = error
```

## 5. Add the guard to a test

Static analysis cannot see a call whose target is decided at run time. The guard watches the SQL EF Core really sends.
In your test project:

```bash
dotnet add package NPlusOne.EntityFrameworkCore
```

Attach it where the test builds its context, then wrap the code under test in a scope:

```csharp
using NPlusOne.EntityFrameworkCore;

var options = new DbContextOptionsBuilder<ShopContext>()
    .UseSqlite(connection)
    .UseNPlusOneGuard()
    .Options;

[Fact]
public async Task Listing_orders_does_not_query_per_customer()
{
    await using var db = new ShopContext(options);
    using var guard = NPlusOneGuard.Begin();

    await new OrderService(db).ListAsync();

    guard.AssertNoNPlusOne();   // throws NPlusOneViolationException with the repeated SQL and where it came from
}
```

## 6. Watch every request in development (optional)

```bash
dotnet add package NPlusOne.AspNetCore
```

```csharp
builder.Services.AddNPlusOneGuard();
builder.Services.AddDbContext<ShopContext>(o => o.UseSqlServer(connectionString).UseNPlusOneGuard());

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseNPlusOneGuard();   // one scope per request; suspects are logged as warnings
}
```

## Where next

- [The analyzer in depth](analyzer.md): what it follows, what it skips, solutions with several projects.
- [The runtime guard](../runtime.md): scopes, options, ASP.NET Core filters, reports.
- [Configuration](configuration.md): every option of both layers.
- [In CI](ci.md): gate pull requests on N+1.
