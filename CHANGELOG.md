# Changelog

## Unreleased

**`NPlusOne.Analyzers`**

- Much cheaper in the IDE. After a keystroke the analyzer no longer reads every file of the project: it keeps what
  depends only on declarations (the entity model, interface implementations, mediator handlers) and what unchanged
  files configure (`AutoInclude`, AutoMapper and Mapster maps) for the next compilation. In a generated project of
  1,000 files (130,000 lines), analysing the edited document went from about 830 ms and 180 MB to 170 ms and 46 MB;
  building it got faster too. The findings are the same.
- Entities mapped only in the model configuration (`modelBuilder.Entity<T>()`, also in an extension method on
  `ModelBuilder`, or `Entity(typeof(T))`) without a `DbSet` are entities: their lazy loads are found.
- A query a repository from a package hands out in a task (`await repository.GetQueryableAsync()`, `GetDbSetAsync()`)
  is an EF query, so running it per element is reported.
- ABP Framework: its repositories (`GetAsync`, `FindAsync`, `GetListAsync`, `DeleteAsync(id)`, `EnsureExistsAsync`,
  explicit loading, `repository.FirstOrDefaultAsync(...)` and the other operators), `AsyncExecuter`, `WithDetails`,
  writes with `autoSave: true` (NP0003), and custom `EfCoreRepository` classes. ABP application layers, which
  reference no EF Core, are analysed.
- Projects that reference no EF Core but call repositories (a Clean Architecture application layer) are analysed:
  NP0006 now appears there, as its rule page always said.
- A navigation read after loading it explicitly (`Entry(o).Reference(x => x.Customer).Load()`) is no longer reported
  as a lazy load.
- An override that queries and then calls `base.Method(...)` is no longer reported as recursive (NP0004).
- A `base.Method(...)` call runs only the base implementation: in a loop it is reported when the base queries or reads
  a navigation, not when only an override does (NP0001, NP0002), and a query it returns keeps the base's includes.
- A local declared `null` and filled later (`List<Order> orders = null; orders ??= query.ToList();`) keeps the query
  as its source, so lazy loads on its elements are reported.
- Less work per keystroke: interface calls no longer collect every candidate type when the cached map answers, call
  arguments are traced only where they are used, lazy-load checks are skipped in projects without lazy loading, and the
  walks over a whole method are made once per method instead of once per question. Typing a method signature no longer
  rebuilds the entity model and the interface map: in a generated project of 1,000 files, a declaration keystroke
  allocates less than half of what it did.
- A referenced assembly whose method summaries are broken (an empty or unknown field) no longer makes the analyzer
  throw (AD0001); the broken lines are skipped.

**`NPlusOne.EntityFrameworkCore`**

- The SQL in reports is read in the provider's dialect (SQL Server, SQLite, PostgreSQL, MySQL, Oracle), under every
  server setting that changes how backslashes and double quotes are read; another provider gets every reading. A literal
  ending in a backslash on SQL Server, SQLite or Oracle, and a double-quoted string on MySQL or SQL Server, are now
  hidden like any other literal, and SQL Server's and SQLite's `LIKE ... ESCAPE '\'` no longer hides the rest of the
  query.
- Call sites cost less: the file and line of a code location are read once, not on every command and lazy load. A
  request with ten queries inside a scope allocates about a third less, and runs about an eighth faster.
- The SQL of a command is normalized once per command text, not on every run: EF Core sends the same text for every
  element of an N+1. Without call sites, a scope now adds a few percent to a request's time. Normalizing a new text
  works in pooled buffers and allocates only the result.
- A scope allocates a fifth of what it did (272 bytes when nothing is recorded), and the provider's dialect and the
  lazy-loaded navigation names are worked out once per context and per navigation instead of on every command.
- New option `AssertThrowsInProduction` (default `false`), read by the ASP.NET Core assert filters.
- An `IgnoredSqlPatterns` regex without a timeout of its own stops after 100 ms on a command (that command is then not
  recorded), so a pattern that backtracks on SQL with inlined values cannot hold the request.

**`NPlusOne.AspNetCore`**

- `[NPlusOneAssert]` and `.WithNPlusOneAssert()` no longer throw in the Production environment: they log the report as
  a warning (event `NPlusOneAssertInProduction`) and the request succeeds, so an assert left on an endpoint cannot let
  its data turn it into an error page. Every other environment (Development, Testing, Staging) still fails the
  request; `AssertThrowsInProduction = true` restores throwing in Production.

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
