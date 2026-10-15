# N+1: design

N+1 finds code that runs one database round trip per element of a collection: one query for the
list, then N more for each item. It works in two layers, because neither can see everything:

| Layer | When | Sees | Cannot see |
|---|---|---|---|
| **Analyzer** (`NPlusOne.Analyzers`) | Every build and as you type | Every code path, including ones no test runs | Calls whose target is only known at run time (an implementation in an assembly the project does not reference, reflection, a serializer walking lazy navigations it was not told about) |
| **Runtime guard** (`NPlusOne.EntityFrameworkCore`, `NPlusOne.AspNetCore`) | Tests and requests, inside a scope | Every query that really runs, wherever it comes from | Code paths that no test or request runs |

The analyzer catches most N+1 problems before the code runs; the guard turns the rest into a
failing test.

## Analyzer

### Rules

| ID | Title | Default |
|---|---|---|
| NP0001 | A query runs once per element (N+1 read) | Warning |
| NP0002 | A lazy-loaded navigation is read once per element | Warning |
| NP0003 | `SaveChanges` runs once per element (N+1 write) | Warning |
| NP0004 | A recursive method runs a query on every call | Warning |
| NP0005 | An endpoint returns entities whose lazy navigations the serializer will load one by one | Warning |
| NP0006 | A data-access abstraction is called once per element and its implementation is not visible | Info |
| NP0007 | A mapper reads lazy navigations of each element by naming convention | Info |

NP0001–NP0005 are reported only when the analysis can prove that code runs a database round trip
and that it runs once per element. NP0006 and NP0007 are the rules for educated guesses and are suggestions
by default; projects raise them to warnings when their code follows the conventions they assume.

### What "runs a query" means

An operation runs a query when it is one of:

- a `System.Linq.Queryable` method that does not return an `IQueryable` (`First`, `Count`, `Any`,
  `Sum`, ...), an EF Core `EntityFrameworkQueryableExtensions` / `RelationalQueryableExtensions`
  method that executes (`ToListAsync`, `FirstOrDefaultAsync`, `CountAsync`, `LoadAsync`,
  `ForEachAsync`, `ExecuteUpdate`, `ExecuteDelete`, ...), or a `System.Linq.Enumerable` method
  (`ToList`, `ToArray`, `ToDictionary`, ...) and `foreach` over a source whose static type is an
  `IQueryable`;
- `DbSet<T>.Find(Async)` and `DbContext.Find(Async)`;
- explicit loading: `ReferenceEntry`/`CollectionEntry`/`NavigationEntry.Load(Async)`,
  `EntityEntry.Reload(Async)`;
- raw SQL: `DatabaseFacade.ExecuteSql*`, and `SqlQuery`/`FromSql*` once executed;
- a call to a method that runs a query on every call (see *Call chains*).

and its source is **provably EF-backed**: following the receiver chain (and single-assignment
locals) back to its root ends at a `DbSet<T>`, `DbContext.Set<T>()`, `Entry(...).Query()`,
`Database.SqlQuery<T>`, or a method whose returned query is EF-backed. A root that is
`AsQueryable()` over an in-memory sequence is not EF-backed. A root that is a parameter makes the
method *conditionally* run a query: it does when a caller passes an EF-backed query.

Anything inside a lambda that is converted to an expression tree is translated into the same SQL
statement (`db.Customers.Select(c => c.Orders.Count())` is one query) and is never a separate
round trip.

### What "once per element" means

An operation runs once per element when it is inside an *iteration context* of the method it is
in, or in a method called from one:

- the body, condition or increment of `for`, `foreach`, `await foreach`, `while` and `do` (not the
  collection expression of `foreach`, which runs once);
- a lambda or method group passed to a method that invokes it per element: every
  `System.Linq.Enumerable` and `System.Linq.AsyncEnumerable` method (`Select`, `Where`,
  `ToDictionary`, ...), `List<T>.ForEach`, `Array.ForEach`, `Parallel.For/ForEach/ForEachAsync`,
  EF Core's `ForEachAsync`, and methods listed in `nplusone.iterating_methods`;
  `Task.WhenAll(ids.Select(...))` is covered by the `Select`;
- a method that calls itself, directly or through other methods, while running a query (NP0004),
  since every level of the recursion is another round trip.

A local function runs where it is called: the call is analysed like any other call, with the local
function's summary.

Loops that make one round trip per iteration on purpose are left alone:

- `foreach` over `Chunk(...)`: one query per batch is the fix, not the problem;
- `for`, `while` and `do` loops whose query is ordered and bounded (`OrderBy` and `Take`: paging and
  keyset loops), that wait (`Task.Delay`, `Thread.Sleep`: polling and retry), or that search: they walk
  no collection (their condition reads no `Length` or `Count`, their body takes nothing from a queue,
  stack or enumerator) and leave as soon as the query answers (the query is the loop condition, or an
  `if` on its result returns or breaks), like generating a code until it is unique;
- load-once guards: `x ??= query`, `if (x is null) { x = query; }`.

### Call chains

Each method in the compilation gets a *summary*, computed on first use and cached per
compilation:

- runs a query on every call (and the call chain that proves it);
- runs a query when parameter *k* is an EF-backed query;
- returns an EF-backed query (and which navigations it `Include`s);
- reads which lazy navigations of parameter *k* (for mappers called per element), of each element
  of a sequence parameter *k* it walks (for helpers handed a whole result), or of `this` (for an
  entity's computed properties and methods);
- invokes delegate parameter *k* once per element (for user-written `ForEach` helpers);
- calls `SaveChanges`.

A call to an interface or virtual method uses the summaries of every implementation in the
compilation; one implementation that runs a query is enough. A call that is not dispatched
(`base.GetAsync(id)`) runs only the method's own body, never an override. A mediator call (`ISender.Send`,
`IPublisher.Publish`) runs the handlers registered for its request type: the `Handle` methods of the
`IRequestHandler`/`INotificationHandler` implementations in the compilation (and the Mediator library's
`ICommandHandler`/`IQueryHandler`). A call through a delegate local runs
what was assigned to it: the summaries of its lambdas (built on the spot) and method groups. A
query field or auto-property is traced through every value its type assigns to it, in initializers,
constructors and methods. Summaries follow calls up to
`nplusone.max_call_depth` (default 4) levels and stop at cycles.

When a loop walks a set of different implementations and calls the interface method on each
element (`foreach (var check in checks) await check.RunAsync()`), each implementation runs once, so
the call is reported only when every implementation makes the round trip.

**Referenced projects.** A method in a referenced project has no body in the compilation. The
`NPlusOneSummaryGenerator` (in the same package) writes each project's summaries into its own
assembly as assembly attributes: per public method, whether it runs a query or saves (always, or
when an argument is an EF query), what it returns, which navigations it reads, which delegates it
calls per element, plus the navigations its model auto-includes and a marker that the assembly's
summaries are complete. The marker carries the format version (`complete:1`): an assembly written in
another format is read as having no summaries, which can miss a round trip but never invents one. A
generator that fails writes nothing rather than breaking the build (CS8785 under warnings-as-errors).
Transitive facts are resolved before writing, so a reader needs no call
edges. The analyzer reads these from referenced assemblies and treats the methods like its own,
including as implementations of interfaces. The generator runs in builds only
(`RegisterImplementationSourceOutput`): IDE live analysis sees calls into other projects as opaque,
the build sees through them. `NPlusOneEmitSummaries=false` in a project turns it off.

When no implementation is visible (the interface lives in a project that does not reference the
implementation, as in Clean Architecture), the call cannot be proven. It is reported as NP0006 when
the method looks like data access (its type matches `nplusone.data_access_types`, by default
`*Repository;*Repo;*Store;*Dao;*DataAccess;*QueryService;*Queries`, and it returns a value), or as
NP0001 when it matches `nplusone.query_methods`.

### Lazy loading

Lazy loading is on when `nplusone.lazy_loading = enabled`, or, with the default `auto`, when the
compilation calls `UseLazyLoadingProxies` or references `Microsoft.EntityFrameworkCore.Proxies`,
or the entity takes an `ILazyLoader` / lazy-loader delegate in its constructor (then only the navigations
whose getter mentions the loader are lazy). A navigation is a
property of an entity type whose type is another entity type or a collection of one; entity types
come from the `DbSet<T>` properties and `IEntityTypeConfiguration<T>` classes the compilation can
see, the types reachable from them through navigations, and the types deriving from an entity type.

NP0002 is reported when a navigation is read on an element inside an iteration context, the
element comes from an EF-backed query (directly or through a method whose summary says so), and
that query did not load the navigation: it did not `Include` it, and the model does not
`AutoInclude()` it (or the query calls `IgnoreAutoIncludes()`). Reads inside expression trees are
translated to SQL and are not lazy loads. Nothing is lazy-loaded after the method sets
`ChangeTracker.LazyLoadingEnabled = false`. Untracked results (`AsNoTracking`,
`AsNoTrackingWithIdentityResolution`; `AsTracking` turns tracking back on) depend on the EF Core version the
compilation references (`KnownSymbols.EfCoreMajorVersion`): before EF Core 8 nothing loads for them; from EF Core
8 on, a lazy navigation loads when the entity holds what the query needs (its key, for a collection or a
reference whose foreign key is on the other side; a foreign key property of its own otherwise), while a
reference with a shadow foreign key throws and is not reported. When a local is assigned from several
queries, a navigation counts as loaded if any of them loads it and the results as untracked if any
of them is, so a lazy load is reported only when every path makes one.

Every corpus case is also run against SQLite (`GroundTruthTests`): a marked method must send more SQL commands
for more seeded rows, an unmarked one the same number. The version-dependent rules above come from those runs
on every supported EF Core version; EF Core 7 and older are out of support and follow their documented behaviour.

### Messages and fixes

Each diagnostic names the query, the loop and the call chain between them, and suggests the
rewrite that removes the extra round trips:

- `Where(x => x.Key == item.Value)` per element: preload with `Where(x => keys.Contains(x.Key))`
  and group with `ToLookup`;
- `Find(id)` per element: preload with `Where(x => ids.Contains(x.Id))` and `ToDictionary`;
- `Count`/`Any`/`Sum` per element: one grouped query (`GroupBy(...).Select(g => new { g.Key, ... })`);
- a lazy navigation per element: `Include` it in the query that produced the elements, or project
  only what is needed with `Select`;
- `SaveChanges` per element: call it once after the loop, or use `ExecuteUpdate`/`ExecuteDelete`.

Code fixes apply two rewrites directly: NP0002 adds the `Include`/`ThenInclude` chain right after the
root of the query that produced the elements (the diagnostic carries the root as an additional
location, so the fix works when the query is in a repository in another file), and NP0003 moves a
`SaveChanges` statement from a loop body to right after the loop. The fixes live in
`NPlusOne.Analyzers.CodeFixes`, so the compiler never loads the Workspaces layer; both assemblies ship
in the `NPlusOne.Analyzers` package.

### Cost in the IDE

An IDE makes a new compilation on every keystroke and analyses the open document in it, so anything the analyzer
learns about the whole compilation would be learned again per keystroke. Three things keep that cheap:

- **One walk per file.** The scans that need other files (endpoint mappings, pipeline behavior registrations,
  `AddNewtonsoftJson`, mapper configurations, `AutoInclude`) read a `SyntaxIndex` built once per syntax tree and kept
  with it: the nodes each scan binds, and a hash of the file's declarations. An IDE keeps the trees of the files that
  did not change, so after an edit only the edited file is walked again, and no scan reads a file's text.
- **The syntax first.** A type without a base list cannot implement an interface or derive from a context, and one
  that declares no `override` cannot be what a virtual call runs; a property of a built-in type is never a navigation.
  These are ruled out before the compiler is asked to resolve base types or signatures.
- **Declarations are kept.** The entity types, the implementers of each interface and the mediator handlers depend
  only on declarations and references, and which navigations an `AutoInclude` names or which map a `CreateMap`
  configures depends only on that file besides. `DeclarationFacts` keeps these by name (assembly position and metadata
  name, never symbols) for the next compilation with the same declarations hash (every token outside bodies and
  property and field initializers, constants included) and the very same referenced assemblies, so typing in a method
  body finds them by name. The entities and the interface map depend on less, the type shapes (the declarations without
  methods, constructors and operators), and are shared by every compilation with the same shapes, so typing a method
  signature keeps them too. Any other edit, or a changed reference, computes them again.

Within a document, the questions about a method's locals (where a local gets its value, which loop declares it,
whether lazy loading is turned off before a read) are answered from one walk of the method (`OperationIndex`), call
graph searches follow each method once per depth, and a message's text is built only when a diagnostic is reported.

`benchmarks/NPlusOne.Benchmarks` measures the analysis of one document after a keystroke in a body and in a
declaration, and of a whole compilation, in a generated application (`KeystrokeBenchmarks`, `BuildBenchmarks`), next
to an analyzer that only subscribes to the same operations; `IncrementalTests` edit one file at a time and check that
an edit that changes the answer is never answered from the previous compilation.

### Configuration

All keys live in `.editorconfig` or `.globalconfig`:

| Key | Default | Meaning |
|---|---|---|
| `nplusone.lazy_loading` | `auto` | `auto`, `enabled` or `disabled` |
| `nplusone.max_call_depth` | `4` | How many calls deep summaries follow |
| `nplusone.query_methods` | (empty) | Methods that run a query on every call, for code the analyzer cannot see. `;`-separated, `*` wildcards, e.g. `MyApp.Domain.IOrderRepository.*` |
| `nplusone.ignored_methods` | (empty) | Methods never treated as running a query (cached lookups) |
| `nplusone.iterating_methods` | (empty) | Methods that invoke their delegate argument once per element |
| `nplusone.data_access_types` | see above | Type name patterns NP0006 treats as data access |

## Runtime guard

A scope (an HTTP request through the middleware, a test through `NPlusOneGuard.Begin()`, or any
manual scope) records every command EF Core runs. Commands are grouped by a *fingerprint* (the SQL
with parameters, numbers, strings and GUIDs replaced) and by call site (the first stack frame
outside EF Core and the guard), so the same SQL issued from two different places does not count as
a repeat. A group that runs more than `RepeatThreshold` times in one scope is an N+1 suspect.
Lazy loads are counted separately through EF Core's `NavigationLazyLoading` event, so a proxy that
loads one navigation per element is reported even when the SQL looks different each time.

When a scope ends with suspects, the guard logs a warning or throws `NPlusOneViolationException`,
depending on the options. Reports contain fingerprints and call sites, never parameter values.

## What neither layer sees

- A query in a code path that no test runs and that goes through an abstraction the analyzer cannot
  resolve. Point `nplusone.query_methods` at the abstraction, or write the test.
- N+1 against something other than EF Core (Dapper, `HttpClient`, gRPC). Planned after v1.
