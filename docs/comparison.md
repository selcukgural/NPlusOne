# N+1 next to other .NET tools

Measured 2026-10-13. What other tools catch N+1 work in EF Core code, how they do it, and how they do on this
repository's corpus.

## The landscape

| Tool | Kind | How it finds N+1 | Lazy loads | Serialized entities | Writes | License |
|---|---|---|---|---|---|---|
| **N+1** (this repository): `NPlusOne.Analyzers` | Roslyn analyzer | Round trips (queries, lazy loads, saves) reached once per element, through calls, interfaces, referenced projects, mediators, mappers, getters, delegates | yes (NP0002) | yes (NP0005) | yes (NP0003) | MIT |
| **N+1**: `NPlusOne.EntityFrameworkCore` / `.AspNetCore` | Runtime guard | Same SQL fingerprint or lazy-loaded navigation from the same call site above a threshold, per scope (test, request) | yes | yes (at run time) | opt-in | MIT |
| [LinqContraband](https://github.com/georgepwall1991/LinqContraband) | Roslyn analyzer, 63 EF Core rules | LC007 database execution in a loop, LC010 `SaveChanges` in a loop, LC039 repeated `SaveChanges`, LC045 navigation read without `Include` | LC045 (any read without `Include`, not only per element) | no | LC010, LC039 | MIT |
| `Microsoft.EntityFrameworkCore.Analyzers` | Roslyn analyzer, ships with EF Core | none (internal API use, interpolated raw SQL) | no | no | no | MIT |
| [EFCore.QueryGuard](https://www.nuget.org/packages/EFCore.QueryGuard) | Runtime interceptor | Same SQL text above a threshold (default 3) per scope; slow and too many queries | as repeated SQL | at run time | statements counted one by one | MIT |
| [DbEye](https://www.nuget.org/packages/dbeye) | Runtime middleware | Identical SQL text in one request (default 2+), per endpoint thresholds; development only | as repeated SQL | at run time | only statements that return rows | MIT |
| [KiwiQuery.EFCore](https://www.nuget.org/packages/KiwiQuery.EFCore) | Test helper | Counts queries in integration tests (`AssertCount`); no N+1 rule of its own | as counts | as counts | as counts | MIT |
| [EfCore.QueryExplain](https://github.com/Sarmkadan/efcore-query-explain) | Runtime interceptor | Same query shape with different parameters per context; marked an early scaffold | not stated | at run time | not stated | MIT |
| MiniProfiler, [Entity Framework Profiler](https://hibernatingrhinos.com/products/efprof), [JetBrains Dynamic Program Analysis](https://blog.jetbrains.com/dotnet/2023/03/02/optimizing-entity-framework-core-database-queries-with-dynamic-program-analysis/) | Profilers | Duplicate or repeated queries shown while the application runs | as queries | at run time | as queries | MIT / commercial / commercial |

The runtime tools all see the same thing at heart: SQL that repeats. N+1's runtime guard adds the call site and the
lazy-loaded navigation to each group, counts one `SaveChanges` once however many statements it sends, sees transactions
started per element, and is checked against every corpus case (`RuntimeGuardTests`).

Among the static tools only LinqContraband targets N+1 directly. It is broader than this analyzer elsewhere (cartesian
explosions, client evaluation, sync over async, raw SQL injection and more), and narrower on N+1.

## LinqContraband on the corpus

`ComparisonTests` (`NPLUSONE_COMPARE=1`) runs LinqContraband 5.17.0 over every corpus folder and scores it method by
method against the ground truth: a method whose markers SQLite proves (more SQL for more rows) is an N+1 to find, a
method without markers one that SQLite proves constant. Only LinqContraband's N+1 rules count (LC007, LC010, LC039,
LC045).

| | Proven N+1 methods found | Proven-constant methods flagged |
|---|---|---|
| N+1 (`NPlusOne.Analyzers`) | 260 of 260 | 0 of 472 |
| LinqContraband 5.17.0 | 55 of 260 (21%) | 17 of 472 |

By kind of N+1 (this analyzer's rule ids):

| | N+1 | LinqContraband |
|---|---|---|
| Query per element (NP0001) | 110 of 110 | 34 of 110 |
| Lazy load per element (NP0002) | 85 of 85 | 18 of 85 |
| Save per element (NP0003) | 11 of 11 | 3 of 11 |
| Serialized lazy entities (NP0005) | 54 of 54 | 0 of 54 |
| Convention mappers (NP0007) | 2 of 2 | 0 of 2 |

Read these numbers with their bias in mind: the corpus was written case by case for this analyzer, so its own score is
100% by construction; the comparison shows which of these proven cases another tool sees, not how either tool does on
code neither has seen. LinqContraband's 17 flags on constant methods are 4 LC007 and 2 LC010 warnings on loops that page, poll, retry, drain or
batch, and 11 LC045 warnings, a rule that is wider than N+1 by design (a navigation read without `Include` may also be
null when nothing loads it): some of those are reads of one entity, or of an entity shared by every element, and one is in
the folder with lazy loading off, where LC045's warning about a null navigation is right.

## The runtime tools on the corpus

`ComparisonTests` also runs every corpus method on SQLite (6 seeded rows per entity) under each runtime tool with its
default settings, attached the way its documentation says, and scores it the same way: a method the ground truth proves
grows should be reported, one it proves constant not. EFCore.QueryGuard 1.0.1 runs its interceptor in one of its scopes per
method; DbEye 1.3.0 collects through its interceptor and decides with its own middleware, run at the end as at the end of a
request; MiniProfiler 4.5.4 profiles each method with its EF Core listener, and a SQL command whose text repeats in one
profile counts as found (what its UI marks as a duplicate). 36 methods cannot run in the harness and count for none.

| | Proven N+1 methods found | Proven-constant methods flagged |
|---|---|---|
| N+1 runtime guard (`NPlusOne.EntityFrameworkCore`, writes included) | 265 of 265 | 0 of 295 |
| EFCore.QueryGuard 1.0.1 | 263 of 265 | 6 of 295 |
| DbEye 1.3.0 | 262 of 265 | 8 of 295 |
| MiniProfiler 4.5.4 (duplicates) | 18 of 265 | 3 of 295 |

The tools that group repeated SQL see nearly every case, lazy loads and serialized entities included, since those are
repeated SQL too. Where they differ:

- **One `SaveChanges` with many rows.** QueryGuard and DbEye count the statements of one `SaveChanges` (which EF Core
  batches into one round trip, committed together) as a repeated query, so a save after the loop is reported, and so is
  an ABP unit of work that saves what the loop changed at its end (6 of their false alarms). The guard here had the same error until the corpus run found it.
- **SQL text that differs per element.** A query built at run time with the element's value as a constant
  (`Expression.Constant(id)`) sends different SQL text each time: QueryGuard and DbEye miss it; the guard here normalizes
  literals and parameters before grouping.
- **Transactions per element** are seen by none of the others; **`ExecuteUpdate`/`ExecuteDelete` per element** are missed by
  DbEye, which records only commands that return rows.
- **Thresholds.** DbEye reports a query run twice (its default `NPlus1Threshold` is 1), so a keyset paging loop of two
  pages, or a timer poll of two ticks, is flagged; the guard here and QueryGuard report the third run.
- **MiniProfiler** is a profiler, not an N+1 detector: its duplicate mark needs the same command text with the same
  parameter values, which an N+1 rarely sends.

The guard's 265 of 265 is again by construction (it is tested against this corpus); the useful part is where the other
tools fall short and why.

## Real projects

`tools/scan.sh` on open-source EF Core applications (2026-10-13, built with the 10.0.105 SDK, see `docs/backlog.md`):

| Project | Findings | Triage |
|---|---|---|
| dotnet/eShop (`dc7ea49`): Catalog.API, Ordering.API, Webhooks.API, Identity.API, OrderProcessor | 8 | all real: `CatalogItems.Find` per ordered item (2); per pending outbox event a `Single` lookup and a `SaveChanges` (6; the saves are the outbox's own per-event bookkeeping, the lookups avoidable) |
| ardalis/CleanArchitecture (`e7c79be`): the template, the sample, MinimalClean | 1 | real, in seed data: an `INSERT` per contributor in a loop |
| jasontaylordev/CleanArchitecture (`bf7665f`): Web | 0 | |
| abpframework/abp-samples: the 82 of 100 EF Core solutions that build | 1 | real, in StudioAiGeneratedProjects/MovieCollectionApp: `GetListAsync` maps each movie with a helper that runs `_genreRepository.FindAsync(movie.GenreId)`. Before ABP support (item 8) the scan found nothing: ABP application layers were not analysed at all |
| nopSolutions/nopCommerce | not scanned | uses linq2db, not EF Core |
