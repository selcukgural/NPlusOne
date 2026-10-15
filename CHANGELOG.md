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

**`NPlusOne.EntityFrameworkCore`**

- The SQL in reports is read in the provider's dialect (SQL Server, SQLite, PostgreSQL, MySQL, Oracle), under every
  server setting that changes how backslashes and double quotes are read; another provider gets every reading. A literal
  ending in a backslash on SQL Server, SQLite or Oracle, and a double-quoted string on MySQL or SQL Server, are now
  hidden like any other literal, and SQL Server's and SQLite's `LIKE ... ESCAPE '\'` no longer hides the rest of the
  query.

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
