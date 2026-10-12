---
sidebar_position: 3
---

# The analyzer

`NPlusOne.Analyzers` is a Roslyn analyzer with a source generator and two code fixes. It runs inside the C# compiler, so
it works in every build, in Visual Studio, Rider and VS Code, and in CI, without configuration.

## What it reports

| Rule | Reports | Default |
|---|---|---|
| [NP0001](../rules/NP0001.md) | A query runs once per element | Warning |
| [NP0002](../rules/NP0002.md) | A lazy-loaded navigation is read once per element | Warning |
| [NP0003](../rules/NP0003.md) | `SaveChanges` (or a transaction) runs once per element | Warning |
| [NP0004](../rules/NP0004.md) | A recursive method runs a query on every call | Warning |
| [NP0005](../rules/NP0005.md) | An endpoint or a serializer gets entities whose lazy navigations it loads one by one | Warning |
| [NP0006](../rules/NP0006.md) | A data-access abstraction whose implementation is not visible is called once per element | Info |
| [NP0007](../rules/NP0007.md) | A mapper reads lazy navigations of each element by naming convention | Info |

## What "once per element" covers

The analyzer reports a round trip when it runs once per element of a collection, reached through:

- **Loops**: `for`, `foreach`, `await foreach`, `while` and `do`, including an index stepped by the loop and parts of
  tuple, key/value pair and record elements.
- **Callbacks**: LINQ to Objects lambdas, method groups, `List.ForEach`, `Parallel.ForEach`, `Task.WhenAll` over a
  `Select`, EF Core's `ForEachAsync`, FluentValidation rules chained on `RuleForEach`, a query's final projection that
  EF Core evaluates on the client, and helpers of your own that call a delegate in a loop.
- **Calls**: private helpers, services, interfaces and virtual methods (to their implementations in the solution),
  extension, generic and default interface methods, local functions, delegates kept in locals and fields, compiled
  queries, property getters and indexers, up to four calls deep by default.
- **Libraries**: MediatR and the source-generated Mediator library (handlers, pipeline behaviors, notifications, stream
  requests), AutoMapper (profiles, `ForMember`, resolvers, converters, `ForPath`, `IncludeMembers`), Mapster, Mapperly.
- **Serialization**: MVC actions, minimal APIs, SignalR hubs and clients, `JsonSerializer`, `JsonConvert`, result
  containers and anonymous objects, streamed results; `[JsonIgnore]` of the serializer that really runs is honoured.
- **Implicit calls**: `ToString` where an entity is formatted, `Equals` and `GetHashCode` where entities are hashed,
  conversion operators, property patterns.

The [catalogue](../catalogue.md) lists every shape it knows, with the test that proves it.

## What it does not report

Loops that make one round trip per iteration on purpose stay quiet: batches (`Chunk`), paging (`OrderBy` with `Skip`
and `Take`), polling with a delay, searches that stop at the first answer, values loaded once and cached, cache
factories with a fixed key, and anything inside an expression tree, which EF Core translates into the same SQL statement.
Lazy loads stay quiet when the query `Include`s the navigation, the model `AutoInclude`s it, lazy loading is off, or the
navigation is not virtual.

When you know better than the analyzer (a loop over at most three items, a deliberate per-item transaction), suppress
the warning where it is:

```csharp
#pragma warning disable NP0001 // at most three regions, each a separate tenant database
foreach (var region in regions) { ... }
#pragma warning restore NP0001
```

## Lazy loading

NP0002, NP0005 and NP0007 are about lazy-loaded navigations. The analyzer assumes lazy loading is on when the project
references `Microsoft.EntityFrameworkCore.Proxies`, or when an entity takes an `ILazyLoader`. Set
`nplusone.lazy_loading` (see [Configuration](configuration.md)) when that guess is wrong, for example in a data project
that does not reference the proxies package itself.

The rules depend on the EF Core version the project references: from EF Core 8 on, entities loaded with
`AsNoTracking()` lazy-load too, each read a new query.

## Solutions with several projects

A repository interface in one project, its implementation in another, a loop in a third: the analyzer follows the call
across projects at build time. The package's source generator writes a summary of each project's methods (which
queries, saves and navigation reads they make) into the project's assembly, and projects that reference it read the
summaries. Add the package to every project of the chain.

Live analysis in the IDE does not run the generator, so a warning that depends on another project's code appears when
you build. To keep a project's summaries out of its assembly (a library you publish, say), set
`<NPlusOneEmitSummaries>false</NPlusOneEmitSummaries>` in its project file.

## Code fixes

- **Add `Include`** (NP0002): adds `.Include(...)`, or `.ThenInclude(...)` for a deeper path, to the query that produced
  the elements, also in another file (a repository method), with the `using` it needs.
- **Move `SaveChanges` after the loop** (NP0003): when the call is a statement directly in the loop body.

## Cost

The analyzer caches per compilation and bounds every walk. On a 70,000-line solution it added under 70 ms per project to
the build. It loads in every compiler since the .NET 8 SDK (Roslyn 4.8).
