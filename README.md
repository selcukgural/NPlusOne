# N+1

[![CI](https://github.com/selcukgural/NPlusOne/actions/workflows/ci.yml/badge.svg)](https://github.com/selcukgural/NPlusOne/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/NPlusOne.Analyzers.svg?label=NPlusOne.Analyzers)](https://www.nuget.org/packages/NPlusOne.Analyzers)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/selcukgural/NPlusOne/blob/main/LICENSE)

Finds N+1 query problems in .NET and EF Core code: at build time with a Roslyn analyzer, and in tests and requests
with a runtime guard. Free and open source (MIT).

```text
OrderService.cs(42,31): warning NP0001: 'IOrderRepository.GetByCustomerAsync' runs a query ('ToListAsync' in
  OrderRepository.GetByCustomerAsync) once per element of the foreach loop over 'customers'. Load the rows for all
  elements up front with one query, Where(o => keys.Contains(o.CustomerId)), and group them with ToLookup(o => o.CustomerId).
```

**Documentation: [selcukgural.github.io/NPlusOne](https://selcukgural.github.io/NPlusOne/)**

## Quick start

**1. Add the analyzer** to every project with EF Core code (EF Core 8, 9 or 10; any SDK since .NET 8):

```sh
dotnet add package NPlusOne.Analyzers
```

**2. Build.** N+1 queries show up as warnings, in the build and in the IDE, each with the rewrite that removes it:

```sh
dotnet build
```

**3. Keep them out.** Raise the rules to errors in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.NP0001.severity = error
dotnet_diagnostic.NP0002.severity = error
dotnet_diagnostic.NP0003.severity = error
dotnet_diagnostic.NP0005.severity = error
```

**4. Catch the rest in tests.** The runtime guard fails a test when the same query or lazy load repeats:

```sh
dotnet add package NPlusOne.EntityFrameworkCore
```

```csharp
var options = new DbContextOptionsBuilder<ShopContext>().UseSqlite(connection).UseNPlusOneGuard().Options;

using var guard = NPlusOneGuard.Begin();
await new OrderService(new ShopContext(options)).ListAsync();
guard.AssertNoNPlusOne();
```

**5. Watch requests in development** with `NPlusOne.AspNetCore`: `builder.Services.AddNPlusOneGuard()` and
`app.UseNPlusOneGuard()`.

The [getting started guide](https://selcukgural.github.io/NPlusOne/docs/guide/getting-started) walks through each step.

## What it finds

N+1 hides in more places than a `foreach` with a query in it. The analyzer follows round trips through:

| | |
|---|---|
| Loops | `for`, `foreach`, `await foreach`, `while`, `do`, indexes and deconstructed elements |
| Callbacks | LINQ lambdas, method groups, `List.ForEach`, `Parallel.ForEach`, `Task.WhenAll(ids.Select(...))`, EF Core's `ForEachAsync`, FluentValidation `RuleForEach`, client-evaluated projections, your own iterating helpers |
| Calls | helpers, services, interfaces and virtual methods (to their implementations, across projects), extension and generic methods, delegates, compiled queries, getters and indexers, recursion |
| Libraries | MediatR and Mediator (handlers, pipeline behaviors, streams), AutoMapper, Mapster, Mapperly, ABP Framework (repositories, `AsyncExecuter`, application layers without EF Core) |
| Lazy loading | navigations read per element, checked against the query's `Include`s and `AutoInclude`s, also in `ToString`, `Equals`, conversions and patterns |
| Endpoints | MVC, minimal APIs, SignalR and serializers given lazily loaded entities, also inside pages and anonymous objects; OData and `[JsonIgnore]` respected |
| Writes | `SaveChanges` and transactions per element |

and it stays quiet where a loop is meant to make one round trip per iteration: batches, paging, polling, search loops,
cached lookups, and anything EF Core translates into the same SQL statement.

Every one of the 142 shapes in the [catalogue](https://github.com/selcukgural/NPlusOne/blob/main/docs/catalogue.md) is a
test that runs on SQLite and counts the SQL: a reported case must send more commands for more rows, a silent one the
same number, on EF Core 8, 9 and 10.

## Rules

| Rule | | Default |
|---|---|---|
| [NP0001](https://github.com/selcukgural/NPlusOne/blob/main/docs/rules/NP0001.md) | A query runs once per element (N+1 read) | Warning |
| [NP0002](https://github.com/selcukgural/NPlusOne/blob/main/docs/rules/NP0002.md) | A lazy-loaded navigation is read once per element (code fix: add the `Include`) | Warning |
| [NP0003](https://github.com/selcukgural/NPlusOne/blob/main/docs/rules/NP0003.md) | `SaveChanges` runs once per element (code fix: move it after the loop) | Warning |
| [NP0004](https://github.com/selcukgural/NPlusOne/blob/main/docs/rules/NP0004.md) | A recursive method runs a query on every call | Warning |
| [NP0005](https://github.com/selcukgural/NPlusOne/blob/main/docs/rules/NP0005.md) | An endpoint returns entities whose lazy navigations the serializer loads one by one | Warning |
| [NP0006](https://github.com/selcukgural/NPlusOne/blob/main/docs/rules/NP0006.md) | A data-access abstraction is called once per element and its implementation is not visible | Info |
| [NP0007](https://github.com/selcukgural/NPlusOne/blob/main/docs/rules/NP0007.md) | A mapper reads lazy navigations of each element by naming convention | Info |

Options (lazy loading, methods the analyzer cannot see into, call depth) are listed in
[configuration](https://selcukgural.github.io/NPlusOne/docs/guide/configuration).

## Packages

| Package | Use it for |
|---|---|
| [NPlusOne.Analyzers](https://www.nuget.org/packages/NPlusOne.Analyzers) | The analyzer, its code fixes and the summary generator that follows calls across projects. A development dependency. |
| [NPlusOne.EntityFrameworkCore](https://www.nuget.org/packages/NPlusOne.EntityFrameworkCore) | The runtime guard: scopes in tests or anywhere, `AssertNoNPlusOne`, reports with normalized SQL and call sites, never parameter values. |
| [NPlusOne.AspNetCore](https://www.nuget.org/packages/NPlusOne.AspNetCore) | One guard scope per request, `[NPlusOneAssert]` for MVC and `.WithNPlusOneAssert()` for minimal APIs. |

## How it compares

Measured on the same proven cases, the analyzer finds 255 of 255 N+1 methods with no false warning; LinqContraband,
the static analyzer with the most N+1 rules we found, finds 55. At run time the guard here, EFCore.QueryGuard and DbEye
all see most cases; the guard alone also sees SQL that differs per element, transactions per element, and counts one
batched `SaveChanges` once. The corpus was written for this tool, so read the
[comparison](https://github.com/selcukgural/NPlusOne/blob/main/docs/comparison.md) for what each number means.

## Contributing

`dotnet build` and `dotnet test` (SDK pinned in `global.json`; the .NET 8, 9 and 10 runtimes run the tests). A missed
N+1 or a false warning starts as a corpus case; see
[CONTRIBUTING.md](https://github.com/selcukgural/NPlusOne/blob/main/CONTRIBUTING.md) and
[how it works](https://github.com/selcukgural/NPlusOne/blob/main/docs/design.md).

## License

[MIT](https://github.com/selcukgural/NPlusOne/blob/main/LICENSE)
