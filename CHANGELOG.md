# Changelog

## Unreleased

**`NPlusOne.Analyzers`**

- Much cheaper in the IDE. After a keystroke the analyzer no longer reads every file of the project: it keeps what
  depends only on declarations (the entity model, interface implementations, mediator handlers) and what unchanged
  files configure (`AutoInclude`, AutoMapper and Mapster maps) for the next compilation. In a generated project of
  1,000 files (130,000 lines), analysing the edited document went from about 830 ms and 180 MB to 170 ms and 46 MB;
  building it got faster too. The findings are the same.

## 1.0.0

The first release.

**`NPlusOne.Analyzers`**: a Roslyn analyzer, two code fixes and a source generator.

- Rules NP0001 (query per element), NP0002 (lazy load per element), NP0003 (`SaveChanges` or a transaction per
  element), NP0004 (recursive query), NP0005 (serialized lazy entities), and the suggestions NP0006 (hidden data access)
  and NP0007 (convention mapping).
- Follows round trips through loops, LINQ lambdas, method groups, `Task.WhenAll`, `Parallel`, helpers, services,
  interfaces and their implementations across projects, extension, generic and default interface methods, delegates,
  compiled queries, getters and indexers, recursion; MediatR and Mediator (handlers, pipeline behaviors, streams),
  AutoMapper, Mapster and Mapperly; FluentValidation `RuleForEach`; MVC, minimal APIs, SignalR, `JsonSerializer` and
  `JsonConvert`, OData and `[JsonIgnore]`.
- Stays quiet on batches, paging, polling, retries, search loops, cached lookups and translated expression trees.
- Code fixes: add the missing `Include`, move `SaveChanges` after the loop.
- Every one of the 132 cases in `docs/catalogue.md` is a test run on SQLite with EF Core 8, 9 and 10.

**`NPlusOne.EntityFrameworkCore`**: the runtime guard. Scopes for tests and code, `AssertNoNPlusOne`, grouping by
normalized SQL, lazy-loaded navigation and call site, writes and transactions on request, reports that never contain
parameter values or literals.

**`NPlusOne.AspNetCore`**: one guard scope per request, `[NPlusOneAssert]` for MVC and `.WithNPlusOneAssert()` for
minimal APIs.
