# Backlog

Work agreed on, in the order we take it. Precision comes first: a false warning costs more trust than a
missed one, so known false positives are fixed before any new detection. Every item starts by adding its
cases to `corpus/` (they fail), then the fix makes them pass. A finished item keeps its section, struck
through, with the date and what was done.

The list comes from a gap probe run on 2026-10-12 against the analyzer at `95ecbb9`: of 12 EF Core
scenarios outside the corpus, 1 was handled, 8 were missed and 3 were false positives.

## ~~1. False positives: lazy loads that cannot happen (NP0002)~~ (done 2026-10-12)

Done: `AutoInclude()` from `OnModelCreating` and `IEntityTypeConfiguration` counts as loaded unless the query
calls `IgnoreAutoIncludes()`; `AsNoTracking*` results are never lazy-loaded (also for NP0005), `AsTracking`
turns tracking back on; reads after `ChangeTracker.LazyLoadingEnabled = false` are skipped. Corpus:
`corpus/Lazy/LoadedWithoutInclude.cs` (5 silent, 3 still reported) and `Endpoints.cs` (`Untracked`).
Red before the fix (6 false positives), green after; counter-check `tools/countercheck/01-false-positives.json`
turns all 6 parts red when undone. The two private applications scanned so far still report their 7 real N+1s.

NP0002 fires where reading the navigation runs no query:

- **`AutoInclude()`**: `modelBuilder.Entity<Invoice>().Navigation(i => i.Customer).AutoInclude()` loads the
  navigation with every query of the entity (unless the query calls `IgnoreAutoIncludes()`).
- **`AsNoTracking()`**: lazy loading does not run for untracked entities; the navigation stays null.
  (`AsNoTrackingWithIdentityResolution()` behaves the same.)
- **`ChangeTracker.LazyLoadingEnabled = false`** set on the context in the same method before the reads.

```csharp
var list = await db.Orders.AsNoTracking().ToListAsync();
foreach (var o in list) { Console.WriteLine(o.Customer?.Name); } // NP0002 today, wrong
```

Plan: read `AutoInclude` calls in `OnModelCreating` and `IEntityTypeConfiguration<T>.Configure` into the
entity model and treat those navigations as included (dropped again by `IgnoreAutoIncludes`); carry a
"tracked" flag in `SourceInfo` that `AsNoTracking*` clears; skip lazy reads in a method that turns lazy
loading off on the same context before them. NP0005 uses the same facts.

## ~~2. Quick wins~~ (done 2026-10-12)

Done: calls through delegate locals follow their lambdas and method groups (in loops and in summaries);
query fields and auto-properties are traced through the values their type assigns; recursion through another
method is NP0004, reported once at the call made by the method that queries; NP0005 also checks
`JsonSerializer.Serialize*`, `JsonConvert.SerializeObject` and SignalR `SendAsync`. Corpus:
`corpus/Reads/StoredAndShared.cs` and `corpus/Endpoints/Serialization.cs` (9 reported, 7 silent). Red before
(9 missed), green after; counter-check `tools/countercheck/02-quick-wins.json` turns all 11 parts red when
undone, two of them on the silent side. The real projects report the same 7 N+1s, nothing new.

- **Stored delegate called in a loop**: `Func<int, Task<int>> count = id => db.Orders.CountAsync(...);`
  then `await count(id)` inside the loop. Trace a delegate local to the lambdas assigned to it and treat
  invoking it as calling them.
- **`IQueryable` field or property set in the constructor or an initializer**:
  `private readonly IQueryable<Order> _big = db.Orders.Where(...);` then `_big.CountAsync(...)` in a loop.
  Trace fields through their assignments in the type's constructors and initializers.
- **Mutual recursion** (A calls B, B queries and calls A): NP0004 only sees a method calling itself.
  Detect a cycle in the call edges of the summaries, bounded by the call depth.
- **Explicit serialization**: `JsonSerializer.Serialize(entities)`, `JsonConvert.SerializeObject`, SignalR
  `SendAsync(..., entities)` with lazy navigations not included. Extend NP0005 beyond endpoint return values.

## ~~3. The most valuable misses~~ (done 2026-10-12)

Done: mediator calls (`Send`, `Publish` on MediatR's and Mediator's `ISender`/`IMediator`/`IPublisher`) follow the
handlers for the request type, at the call and inside helpers; navigations read on `this` by an entity's computed
properties and methods are reported where an element reads or calls them; a sequence of entities passed to a
method that walks it (directly or through a mapper method group) reports the navigations read per element, while
a single element (`First()`) does not. Corpus: `corpus/Reads/Mediator.cs` and `corpus/Lazy/ComputedAndPassed.cs`
(9 reported, 8 silent). Red before (8 missed); the helper-path mediator case and the `First()` case were added
after the first green run, and the counter-check `tools/countercheck/03-mediator-computed-passed.json` shows all
10 parts guarded, one of them on the silent side. The real projects report the same 7 N+1s; none of them uses
MediatR, so that path is checked on the corpus only.

- **MediatR (and similar mediators)**: `await sender.Send(new GetOrderTotal(id))` in a loop, where
  `GetOrderTotalHandler.Handle` queries. Resolve `ISender.Send`/`IMediator.Send` to the
  `IRequestHandler<TRequest, TResponse>.Handle` implementations for the request's type in the compilation,
  and follow them like any call.
- **Computed entity properties that read navigations** (common in DDD models):
  `public decimal Total => Lines.Sum(l => l.Amount);` read per element (`list.Sum(i => i.Total)`) lazily loads
  `Lines` once per invoice. Summarize navigation reads on `this` in property getters and instance methods,
  and apply them where an element of an EF query calls them.
- **An EF result passed to a method that reads navigations of its elements**: `PrintCustomers(list)`, where
  `PrintCustomers` loops over its parameter and reads `o.Customer`. Summarize "reads navigation N of the
  elements of parameter k" and report at the call when the argument comes from a query without the include.

## ~~4. Implementations in referenced projects (source generator)~~ (done 2026-10-12)

Done: `NPlusOneSummaryGenerator` writes each project's resolved method facts (queries always or per argument,
saves, returns, navigation reads, per-element delegates), its auto-included navigations and a completeness marker
into the assembly, in builds only; the analyzer reads them for calls, interface implementations and NP0006. The
harness compiles multi-project corpus folders (`projects` file) through the generator; corpus
`corpus/CrossProject` (8 reported, 4 silent). Red before (7 missed, 5 false positives), green after; a real
two-project MSBuild with the packed package reports through the summaries and stops when
`NPlusOneEmitSummaries=false`. Re-scanning the real projects found 5 more real N+1s in a private application's CLI and
desktop app (calls into its infrastructure project) and one false positive, a loop over different health-check
implementations; fixed by requiring every implementation to query when the loop element is the receiver
(`corpus/Reads/Strategies.cs`). Counter-check `tools/countercheck/04-referenced-projects.json`: all 13 parts red
when undone, 5 of them on the silent side.

A service project that references the data project sees `OrderRepository` only as metadata: no body, so no
summary, and the call gets the NP0006 guess at best. Plan: a source generator in the same package writes
each project's summaries (which public methods run a query, on which parameters, which navigations they read)
into assembly attributes; the analyzer reads them from referenced assemblies. Opt-out with an MSBuild
property. It cannot help when the project does not reference the implementation at all (Clean Architecture's
application layer); that stays NP0006, `nplusone.query_methods` and the runtime guard.

## ~~5. AutoMapper and similar mappers (heuristic)~~ (done 2026-10-12)

Done: AutoMapper `Map` and Mapster `Adapt` calls on a result (or on an element in a loop) report the navigations
the mapping reads per element. A visible `CreateMap<TSource, TDestination>()` with its `ForMember` `MapFrom` and
`Ignore` overrides makes them NP0002 warnings; otherwise (profile in another project, Mapster) the naming
conventions are applied and reported as the new Info rule NP0007. `ProjectTo` and single entities outside loops
stay silent. Corpus: `corpus/Mapping` (5 reported, 6 silent). Red before (5 missed), green after; counter-check
`tools/countercheck/05-mappers.json`: all 7 parts red when undone, 2 of them on the silent side.

`mapper.Map<List<OrderView>>(orders)` where `OrderView.CustomerName` flattens `Customer.Name`: AutoMapper reads
`Customer` on every element. Match destination member names to navigation paths by AutoMapper's flattening
convention; `ProjectTo` translates to SQL and stays silent. Lower confidence than the other rules, so it may
need its own rule id or an option.

## ~~6. Real libraries, real projects, real scenarios~~ (done 2026-10-13)

Closed: the real packages are referenced by the corpus (MediatR, AutoMapper, Mapster, Mediator, FluentValidation, OData,
Mapperly, SignalR, Newtonsoft); the known-N+1 catalogue is `docs/catalogue.md`, every case proven on SQLite; open-source
applications are scanned and triaged (eShop, both Clean Architecture templates); the runtime guard and other tools are
measured on the corpus (`docs/comparison.md`). What is left of it moved to item 7.

Items 3 and 5 were checked against stand-ins that copy the shape of MediatR, AutoMapper and Mapster, and the
real-project scans so far (four private applications) use none of them. Next:

Batch 1 done (2026-10-12): the test project references the real MediatR 12.5, AutoMapper 14 (the last MIT release;
its run-time advisory is suppressed there because the corpus is only compiled), Mapster 7.4 and Mediator 3.0
packages, and the stand-ins are gone; the existing corpus stayed green. New corpus cases with the real APIs
(`Reads/MediatRVariants.cs`, `Reads/MediatorLibrary.cs`, `Mapping/RealApis.cs`, `LazyLoader/`,
`Endpoints/HubsAndViews.cs`, `Reads/Loops.cs`) were red (13 missed, 6 false positives) and are green after: the
Mediator library's `ICommandHandler`/`IQueryHandler`; Mapster's `IMapper` and its `TypeAdapterConfig` `Map`/`Ignore`
overrides (NP0002 instead of the NP0007 guess); AutoMapper `ForMember` after other chained calls, up to `ReverseMap`;
only the navigations of an `ILazyLoader` entity whose getter asks the loader are lazy; typed SignalR clients and hub
method results; minimal API handlers passed as method groups; `new OkObjectResult(...)`/`new JsonResult(...)`;
`View`/`PartialView` no longer reported; `DbSet.AsAsyncEnumerable()` traced as the set's query. Counter-check
`tools/countercheck/06-real-libraries.json`: all 16 parts red when undone, 6 of them on the silent side. The real
projects report the same findings as before. `docs/catalogue.md` lists every known N+1 path (128 cases) with its
status; its gaps are the work list from here, after a SQLite run-time check of every case.

- **Real packages.** Build a scenario solution under `samples/` (or a corpus folder whose harness references the
  real NuGet packages) with the real MediatR, AutoMapper (profiles, `ProjectTo`), Mapster, EF Core proxies and
  `ILazyLoader`, ASP.NET Core controllers and minimal APIs, SignalR, Hot Chocolate (GraphQL resolvers, to see what
  is missed there), and check that every known N+1 pattern in them is reported and every correct one is not.
- **The known N+1 catalogue.** Write the patterns the community knows (EF Core docs, blog posts, Stack Overflow:
  lazy loading in views/serializers, per-row `Find`, `Count()` per parent, `SaveChanges` in loops, explicit
  `Load` per item, `Any()` per row, GraphQL resolvers without DataLoader, AutoMapper `Map` after `ToList`, ...) as
  scenarios, each with the fix that must stay silent.
- **Real open-source projects.** Scan EF Core applications that use these libraries (eShop's Ordering and
  Catalog, nopCommerce, Clean Architecture templates such as jasontaylordev/CleanArchitecture and
  ardalis/CleanArchitecture, ABP samples) with `tools/scan.sh`; triage every finding as real or false, turn each
  false one into a corpus case and fix it the usual way (red, green, counter-check). Building a cloned project
  runs its MSBuild targets: only scan well-known projects, and ask before running unfamiliar code.
- ~~**Run-time ground truth.**~~ (done 2026-10-12) `GroundTruthTests` compiles each corpus folder, runs every
  method on SQLite with lazy-loading proxies at 3 and 6 seeded rows per entity (minimal API lambdas through a real
  web application, SignalR and the generated Mediator through fakes) and counts the SQL: 276 methods measured on
  EF Core 8, 9 and 10, every marker agrees; 17 cannot run and each says why. The first runs found four analyzer
  errors, now fixed: from EF Core 8 on no-tracking entities do lazy-load (each read a new query; a reference
  with a shadow foreign key throws instead), so the item 1 rule now depends on the referenced EF Core version;
  a collection back-reference (`Customer.Orders` under `Order.Customer`) is queried per element when serialized;
  `sender.Send(request)` with an `object` local was missed. Counter-check `tools/countercheck/07-ground-truth.json`:
  all 5 parts red when undone. ~~The runtime guard on the same scenarios is still to do.~~ (done 2026-10-12, see below)

.NET 9 (2026-10-12): the runtime packages target net8.0, net9.0 and net10.0 (EF Core 8, 9 and 10), and every test
project, the ground truth included, runs on all three.

Catalogue C9 and C21 (2026-10-12): NP0005 skips navigations the serializer never reads: System.Text.Json's
`[JsonIgnore]` (no condition or `Always`) for endpoints, SignalR and `JsonSerializer`, Newtonsoft's `[JsonIgnore]` and
`[IgnoreDataMember]` for `JsonConvert`; the test project references Newtonsoft.Json. Corpus `Endpoints/JsonIgnored.cs`
was red with 6 false positives and is green after; SQLite showed that `WhenWritingNull` and .NET 10's `WhenWriting`
still load the navigation (both stay reported). Counter-check `tools/countercheck/08-json-ignore.json`: all 10 parts
red when undone. The real projects report the same findings as before. New gap C28: Newtonsoft MVC endpoints.

Catalogue B20 and B21 (2026-10-12): NP0002 follows elements read by a `for` loop's stepped counter (`orders[i]`,
arrays, backwards, through a local) and the parts of tuple and key/value pair elements (`foreach` deconstruction,
`pair.Order`, `pair.Value`) back to the `Select`, `ToDictionary` or `Index` call that built them, so a part shared by
every element stays silent. Corpus `Lazy/IndexedAndDeconstructed.cs` was red with 8 missed and is green after; SQLite
agrees with every marker on EF Core 8, 9 and 10. Counter-check `tools/countercheck/09-index-and-deconstruction.json`:
all 21 parts red when undone, 4 of them on the silent side. The real projects report the same findings as before.
The rarer shapes are the new gap B28.

Catalogue C11 (2026-10-12): NP0005 unwraps `Results.Json`, `TypedResults.Json` and MVC's `Json(data, options)`, whose
value parameter is `data`; the old fallback took the last argument, an omitted optional one (`statusCode`) or the
serializer settings. Corpus `Endpoints/Endpoints.cs` and `Endpoints/HubsAndViews.cs` were red with 4 missed and are
green after; SQLite agrees on EF Core 8, 9 and 10 (25 -> 85 commands for `Results.Json` of customers, 1 -> 1 for the
projection and the fully included query). Counter-check `tools/countercheck/10-results-json.json`: its part red when
undone.

Catalogue C7 and C8 (2026-10-12): NP0005 looks into result containers built where they are returned or serialized:
pages and envelopes (eShop's `PaginatedItems<T>`, records, object initializers), anonymous objects, containers nested
three levels deep, and a local holding one. It follows what the serializer writes: every anonymous member, the public
properties an initializer sets, and the constructor arguments whose parameter names a public property; an argument
only counted, or kept in an internal or `[JsonIgnore]` property, stays silent. Corpus `Endpoints/Containers.cs` was red
with 10 missed and is green after; SQLite agrees on EF Core 8, 9 and 10 (22 -> 61 commands for a page of orders,
1 -> 1 for the silent shapes). Counter-check `tools/countercheck/11-result-containers.json`: all 9 parts red when
undone, 2 of them on the silent side.

Catalogue D9 to D12 (2026-10-12): AutoMapper maps are read further: value resolvers and member value resolvers
(`MapFrom<TResolver>()`, the navigations their `Resolve` reads; NP0001 when it queries, once per mapped element), type
converters and conversion lambdas (`ConvertUsing`, which replaces the whole map, so its conventions no longer count),
`IncludeMembers` (members the source cannot fill come from the included member), `ForPath` (its `MapFrom` reads;
SQLite showed that an ignored `ForPath` drops the root member's convention, so `Customer` is not loaded), and `Map`
passed as a method group to `Select`. Corpus `Mapping/AutoMapperMembers.cs` was red with 8 missed and 3 false
positives and is green after; SQLite agrees on EF Core 8, 9 and 10 (4 -> 7 commands per lazy navigation, 10 -> 37 for
the querying resolver). Counter-check `tools/countercheck/12-automapper-members.json`: all 16 parts red when undone, 2
of them on the silent side.

Catalogue A25 and A26 (2026-10-12): a method of the project called in the selector of a query's outermost `Select`
runs on the client once per row: its queries are NP0001 and the lazy navigations it reads on the row's entities NP0002
(includes and `AsNoTracking()` count as usual). SQLite showed both happen, also a query on the same context inside the
projection (10 -> 37 commands); a second context on the harness's shared in-memory connection cannot run, so that case
says why. A method in `Where` makes EF Core refuse the query and stays silent. Corpus `Lazy/ClientProjection.cs` and
`Reads/ClientProjection.cs` were red with 6 missed and are green after. Counter-check
`tools/countercheck/13-client-projection.json`: all 5 parts red when undone, 1 of them on the silent side.

Catalogue D4, D5 and C19 (2026-10-12): MediatR's `Send` also runs the registered pipeline behaviors whose constraints
accept the request (registration read from `typeof(...)`, `AddBehavior<...>()` and `AddOpenBehavior(...)`; an
unregistered behavior stays silent), and `CreateStream` goes to `IStreamRequestHandler<,>`. NP0005 checks what a hub
method or action streams: `yield return` items and the writes to the channel whose reader a hub method returns, each
item one of many. `WithCancellation`/`ConfigureAwait` on an async sequence walk the same query. The ground-truth
harness now registers what a corpus method taking `MediatRServiceConfiguration` registers, and reads a returned
`ChannelReader<T>` item by item like SignalR. Corpus `Reads/MediatRPipeline.cs` and `Endpoints/StreamingHub.cs` were
red with 5 missed and are green after; SQLite agrees on EF Core 8, 9 and 10. Counter-checks
`tools/countercheck/14-mediatr-pipeline.json` (8 parts, 3 on the silent side) and `tools/countercheck/15-streaming.json`
(5 parts, 1 on the silent side): all red when undone.

Catalogue D16 (2026-10-12): the predicate of `Must`, `MustAsync`, `Custom` and `CustomAsync` chained on FluentValidation's
`RuleForEach` runs once per item; on `RuleFor` once per object (silent). The test project references FluentValidation
12.1.1 (Apache-2.0). A rule lives in the validator's constructor, which the ground truth cannot run, so the new
`// ground-truth-proves: NP0001 (where)` directive lets the method that validates prove it: 11 -> 38 commands on SQLite,
the `RuleFor` validator 1 -> 1. Corpus `Reads/Validation.cs` was red with 1 missed and is green after. Counter-check
`tools/countercheck/16-fluentvalidation.json`: all 3 parts red when undone, 1 of them on the silent side.

Catalogue B14, B15, B8 and A19 (2026-10-12): reading a property or an indexer runs its getter, in loops and in summaries
(an assignment runs only the setter); a call through a delegate field or auto-property follows what the type assigns
to it, like a local; `EF.CompileQuery`/`CompileAsyncQuery` delegates run their query on every call. SQLite agrees
(10 -> 37 commands each, silent cases 1 -> 1); a static compiled query stays bound to the first model and the harness
builds one per run, so that case says why and instance fields prove the rest. Corpus `Reads/Accessors.cs` was red with
7 missed and is green after. Counter-check `tools/countercheck/17-accessors-and-delegate-fields.json`: all 7 parts red
when undone, 1 of them on the silent side.

Catalogue A37 (2026-10-12): a type deriving from an entity is an entity (a hierarchy with a `DbSet` for its base only),
so the derived type's own navigations count. The ground-truth seeder leaves a hierarchy's discriminator to EF Core.
Corpus `Lazy/DerivedTypes.cs` was red with 1 missed and is green after; SQLite: 4 -> 7 commands, included and scalar
reads 1 -> 1. Counter-check `tools/countercheck/18-derived-types.json`: its part red when undone. The real projects
report the same findings as before.

Catalogue B16, B17, B19, B22, B23 and B24 (2026-10-12): NP0002 follows members that run without being named: `ToString`
where an entity is formatted (interpolation, `string.Join`/`Concat`/`Format`, `Console`/`TextWriter` `Write`,
`StringBuilder.Append`; a record's generated printer reads every navigation), `Equals`/`GetHashCode` overrides where
entities are hashed (LINQ's set operators, `new HashSet<T>`), user-defined conversion operators, property patterns
(the pattern's input resolved to the matched value, nested and in `switch`), and a loop that walks a local up its own
lazy navigation. SQLite showed a record's generated equality compares fields and loads nothing (silent). Corpus
`Lazy/ImplicitReads.cs` was red with 11 missed and is green after; every marker grows 4 -> 7 except the chain walk,
which the seeder's two-level trees cannot drive (it says why). Counter-check `tools/countercheck/19-implicit-members.json`:
all 13 parts red when undone, 1 of them on the silent side.

Catalogue A23 (2026-10-12): `Database.BeginTransaction`/`BeginTransactionAsync` (and the relational overloads with an
isolation level) per element is NP0003, with its own message (start one transaction around the loop). The ground-truth
log now counts the transactions the code starts (not the one SaveChanges opens). Corpus `Writes/Transactions.cs` was red
with 2 missed and is green after; SQLite: 11 -> 38 commands, one transaction around the loop 3 -> 3. The move-SaveChanges
code fix is not offered for a transaction (test). Counter-check `tools/countercheck/20-transactions.json`: both parts red
when undone.

Catalogue C23 and C28 (2026-10-12), the last false-positive gaps: NP0005 skips OData actions (`[EnableQuery]` on the
action or its controller), whose formatter writes only structural properties and expanded navigations; and checks MVC
results against Newtonsoft's ignore attributes when the application calls `AddNewtonsoftJson` (minimal APIs stay
System.Text.Json). The test project references Microsoft.AspNetCore.OData 9.5.0 and Microsoft.AspNetCore.Mvc.NewtonsoftJson
(per framework); the ground truth writes a Newtonsoft folder's controller results with Newtonsoft. The OData cases say
why they cannot run (the harness has no OData formatter). Corpus `Endpoints/OData.cs` was red with 3 false positives and
`EndpointsNewtonsoft/NewtonsoftMvc.cs` (a new folder) red with the Newtonsoft-ignored navigation reported; both are
green after; SQLite agrees on the Newtonsoft folder (`Writers` 1 -> 1 through Newtonsoft, 10 -> 37 through the minimal
API). Counter-checks `tools/countercheck/21-odata.json` and `tools/countercheck/22-newtonsoft-mvc.json`: all 4 parts red
when undone, 3 of them on the silent side.

Catalogue D15 (2026-10-12): Mapperly's mapper methods are partial methods whose bodies its generator writes; a partial
method is now summarized from its implementation part, so the generated reads count like hand-written ones. The test
project references Riok.Mapperly 4.3.1 (Apache-2.0) and the corpus compilation runs its generator (copied next to the
tests) along with the summary generator. Corpus `Mapping/Mapperly.cs` was red with 2 missed and is green after; SQLite:
4 -> 7 commands per element and for the whole list, the ignored member and the included query 1 -> 1. Counter-check
`tools/countercheck/23-mapperly.json`: its part red when undone.

Catalogue B28 (2026-10-12), the last missed gap: parts of elements are followed in a lambda over the elements
(`pairs.Sum(p => p.Order...)`), through `Zip`, key/value pairs and objects built in `Select` (a record's positional
parameters, constructor arguments named like the member, initializers; deconstructed with the type's `Deconstruct` or
read as a property), and an index stepped in a `while` loop's body. A part shared by every element stays silent, as
does an index that does not use the stepped local. Corpus `Lazy/MoreElementParts.cs` was red with 7 missed and is green
after; SQLite: 4 -> 7 commands each, the shared cases constant (a `Zip` with the customers themselves was not driven by
the count: loading them fixes up every order's `Customer`, so the case zips with positions). Counter-check
`tools/countercheck/24-more-element-parts.json`: all 10 parts red when undone, 2 of them on the silent side.

The catalogue's 33 "works" rows (2026-10-12) are corpus cases now, both sides, each run on SQLite:
`Reads/WorksCases.cs` (operators, set-based statements, raw SQL, entry round trips, LINQ lambdas and query syntax,
extension, generic and default interface methods, generic repositories, run-time dispatch and expression trees, and
the silent entry, `Local` and `EF.Property` shapes), `Lazy/WorksLazy.cs` (filtered include, split query, owned types,
skip navigations, null-conditional reads, string `Include`), `Writes/WorksWrites.cs` and `Endpoints/WorksEndpoints.cs`.
One row was wrong: D21, a cache factory with a fixed key (`cache.GetOrCreate("orders", _ => db...)`) in a loop, was
reported although SQLite shows it runs once; the factory of `GetOrCreate*`/`GetOrAdd*`/`GetOrSet*` now counts as per
element only when its key reads something declared in the loop (a key per element misses once per element: still
reported, 10 -> 37). Counter-check `tools/countercheck/25-cache-factories.json`: all 3 parts red when undone, 2 of them
on the silent side. Every catalogue row is now covered (118), a runtime-guard row (10) or not N+1 (3).

The runtime guard on the corpus (2026-10-12): `RuntimeGuardTests` runs every corpus method once more, on 6 rows per
entity, with the guard on its contexts and in a guard scope (writes included): the guard must report a suspect for every
method the ground truth proves grows and none for one it proves constant. The first run found two guard errors, both
fixed: one `SaveChanges` sending many statements (EF Core batches them, they commit together) was a repeated write, so a
save after the loop was reported (4 cases); and a transaction started per element was not seen. A `SaveChanges` call now
records its first statement only, and a transaction the code starts is a write (the one `SaveChanges` opens is not; the
call is tracked per context, since EF Core runs several interceptors through an async composite an async-local does not
survive). Every asserted corpus method now agrees with the guard on EF Core 8, 9 and 10. Counter-check
`tools/countercheck/26-guard-writes.json` (run with `RuntimeGuardTests` and with the guard's own `GuardTests`, which gain
three cases): all 3 parts red when undone, 2 of them on the silent side.

Open-source projects and other tools (2026-10-13): `tools/scan.sh` used to hide a failed build, so a project whose
`global.json` asks for an SDK this machine lacks "had no findings" without being compiled; it now pins such a copy to the
SDK this repository builds with (and says so), and fails loudly when the build does not get through. That showed one private
application's API project had never been compiled in a scan (a `bin\Debug` folder name broke MSBuild's globs on macOS; the copy now
leaves names with a backslash out): compiled now, it adds no finding. eShop (8 findings,
all real: `Find` per ordered item, per outbox event a `Single` lookup and a `SaveChanges`), ardalis/CleanArchitecture (1,
real: an `INSERT` per row in seed data) and jasontaylordev/CleanArchitecture (0); nopCommerce uses linq2db. Next to other
.NET tools: `docs/comparison.md`, with `ComparisonTests` (`NPLUSONE_COMPARE=1`) scoring LinqContraband on the corpus
(51 of 229 proven N+1 methods, 14 of 410 proven-constant methods flagged; 53 of 231 and 17 of 415 after the review below). The runtime tools run through the same harness
(`IRuntimeDetector`; the guard's own corpus run uses it too): EFCore.QueryGuard 232 of 234 with 4 false alarms, DbEye 231
with 5, MiniProfiler's duplicates 17 with 1, the guard here 234 with none; they miss SQL text that differs per element,
transactions, and (DbEye) set-based statements, and count one batched `SaveChanges` as repeats.

Review before 1.0 (2026-10-13). Runtime guard: SQL fingerprints are scanned by hand for every dialect's literals
(backslash escapes, `N''`/`E''`/`X''`, dollar quotes, nested comments), so no value reaches a report; a `SaveChanges`
counts once however many rows it writes, and a transaction it opens is not counted; a transaction per element is; a
cancelled save, a disposed outer scope and a context without the guard are handled; recording never fails a command;
WebSocket requests are not watched. Analyzer: a poll on a `PeriodicTimer`, a save retried in a `try` (a loop that walks
no collection), a loop that reads a page per iteration and a loop over a failed save's conflict entries are not N+1,
while a `try` around each element's save still is (`corpus/Writes/RetriesAndDrains.cs`, 2 reported, 6 silent); the
SaveChanges fix is offered only for a `foreach` whose save uses nothing declared in the loop; the Include fix goes after
`FromSql*`; an application assembly whose name starts with `System` or `Microsoft` is no longer taken for the framework
(`corpus/CrossProject/SystemData`); summaries carry a format version and a failing generator writes nothing; a field's
assigned source is never read half-built by another thread. Counter-checks `tools/countercheck/26-guard-writes.json`,
`27-guard-review.json` and `28-analyzer-review.json`: every part red when undone.

## 7. After 1.0 (next)

- ~~**IDE performance.**~~ (done 2026-10-13) `PerformanceTests` (`NPLUSONE_BENCH=<modules>`) generates an application
  and times one document's analysis after a keystroke, next to an analyzer that only subscribes to the same operations.
  Measured first: per keystroke the analyzer rebuilt the entity model (base types of every type, every property type),
  scanned every implementation candidate for each interface or virtual call, and bound every mapper profile and
  `AutoInclude` file, all linear in the project's size. Now the whole-compilation scans read a per-tree `SyntaxIndex`
  (only the edited file is walked again, no file's text is read), types are walked for nested types only, the syntax
  rules types out before the compiler resolves them (no base list, no `override`, built-in property types), and
  `DeclarationFacts` keeps by name what depends only on declarations (entities, interface implementers, mediator
  handlers) and what unchanged files configure (`AutoInclude`, which map each `CreateMap` configures) for the next
  compilation with the same declarations hash and references. At 1,000 modules (130,000 lines in one project) a
  keystroke went from 830 ms and 180 MB to 170 ms and 46 MB (floor 45 ms); at 2,000 from 1.8 s to 0.26 s, and analysing
  the whole compilation from 15 s to 12 s. Same findings on the corpus and the benchmark. `IncrementalTests` edit one
  file at a time (an added `DbSet`, a moved base list, a changed constant, a changed referenced project, partial and
  nested types); counter-check `tools/countercheck/29-ide-performance.json`: all 13 parts red when undone.

- ~~**Fingerprint dialects.**~~ (done 2026-10-15) The fingerprint read every command one way: a backslash escaped a
  quote and a double-quoted plain word was a name. On SQL Server, SQLite and Oracle a backslash is an ordinary character,
  so after a literal ending in one (`N'C:\'`) the next literal was taken for code and shown; on MySQL (and SQL Server
  with `QUOTED_IDENTIFIER` off) `"Alice"` is a string and was shown; SQL Server's `ESCAPE N'\'` hid the rest of the
  query. Now the interceptor passes the provider's `SqlDialect` (`Database.ProviderName`), and the scan runs once per
  reading of backslashes the dialect allows under any server setting (MySQL's `NO_BACKSLASH_ESCAPES`, PostgreSQL's
  `standard_conforming_strings`; every reading for another provider), hiding each character any reading takes for a
  value. Double-quoted plain words stay names on PostgreSQL, SQLite and Oracle only. Tests: `SqlFingerprintTests`
  (both sides per dialect; red before, 19 failures). Counter-check `tools/countercheck/32-fingerprint-dialects.json`
  (run with `--project tests/NPlusOne.EntityFrameworkCore.Tests --filter FullyQualifiedName~NPlusOne.EntityFrameworkCore.Tests`):
  all 10 parts red when undone.
- **ABP samples.** Scanned 2026-10-13, see item 8: the scan found nothing because the analyzer could not see ABP code.
- **GraphQL resolvers (catalogue C24).** Hot Chocolate type-extension resolvers taking `[Parent]` run once per parent
  without a DataLoader; see what the analyzer misses there and decide whether to follow them before Dapper and ADO.NET.
- **A scenario solution under `samples/`.** One small application wiring the libraries together the way a real one would
  (the corpus covers each case on its own); useful for the documentation and as a demo.
- **More open-source applications** as users name them; each false finding becomes a corpus case.
- **New majors of the corpus libraries.** The corpus compiles against AutoMapper 14, MediatR 12.5 and Mapster 7.4
  (Dependabot ignores their majors). Check AutoMapper 15+/16, MediatR 13+/14 (both commercially licensed now) and
  Mapster 10 for API shapes the analyzer does not follow yet, with corpus cases compiled against them.

## 8. ABP Framework (in progress)

`tools/scan.sh` over abpframework/abp-samples (2026-10-13): of 100 solutions with EF Core projects, 82 built and
reported nothing, 18 did not build (ABP Commercial, Telerik and DevExpress feeds, samples broken on their own, an old
`protoc` without arm64, the retired Aspire workload). Eight N+1s planted in a copy of BookStore-Mvc-EfCore showed why
nothing was reported: only `context.Set<Author>().FindAsync(id)` in the EF Core project was found. The gaps:

- ~~**Entities mapped only in the model configuration.**~~ (done 2026-10-13, catalogue A45) `builder.Entity<Book>(...)`
  in `OnModelCreating` or in a module's `ConfigureBookStore(this ModelBuilder builder)`, without a `DbSet`, did not make
  `Book` an entity. Now every `ModelBuilder.Entity<T>()` and `Entity(typeof(T))` call maps one; the files are found
  through the `SyntaxIndex`, bound once per unchanged file, and the entities are reused only for the same mapped types.
- ~~**A query handed out in a task.**~~ (done 2026-10-13, catalogue D24) a bodyless `Task<IQueryable<Book>>` or
  `Task<DbSet<Book>>` (ABP's `GetQueryableAsync()` and `GetDbSetAsync()`) was not an EF query; only a plain
  `IQueryable<Entity>` was.
- ~~**Projects without EF Core.**~~ (done 2026-10-13, catalogue D29) The analyzer started only when the compilation had
  EF Core's `DbContext`. An ABP application layer, and any layered application whose repositories live behind
  interfaces in the domain project, references no EF Core, so it was never analysed: not even the NP0006 the rule page
  promised for Clean Architecture. Now it also starts with ABP's repositories, with `nplusone.query_methods`, or when
  the project or a referenced non-framework assembly declares a type matching `nplusone.data_access_types` (read from
  the assemblies' type names, nothing is bound); a project with no data access still costs nothing. Corpus:
  `corpus/Layered` (two projects, compiled without EF Core and ABP).
- ~~**ABP's own APIs.**~~ (done 2026-10-13, catalogue D25 to D30, A46) `AbpCalls` knows the repositories' query methods,
  the repository operators, explicit loading, `AsyncExecuter`, `WithDetails` and the writes with `autoSave: true`; any
  repository-typed expression is an entity set, and `IEntity` marks the entities where no `DbContext` is visible.
  `includeDetails: true` and `WithDetails()` load details the application configures, which the analysis cannot read:
  every navigation counts as loaded there (`SourceInfo.AnyInclude`). A method the application declares with a body of its
  own (a custom repository's override) is followed through it. A navigation read after an explicit load of it
  (`Entry(o).Reference(...).Load()`, ABP's `EnsurePropertyLoadedAsync`) was a false NP0002, for EF Core too; fixed.
  Corpus: `corpus/Abp` (an application layer compiled without EF Core) and `corpus/AbpEfCore` (a DbContext, a custom
  `EfCoreRepository`, a module configuring details), compiled against ABP 8.3, 9.3 and 10.7; the ground truth runs them
  in an ABP application (Autofac, ABP's `EfCoreRepository`, a unit of work per method) on SQLite. Counter-check
  `tools/countercheck/30-abp.json`.

- ~~**Scan abp-samples again.**~~ (done 2026-10-13) The 82 solutions that build report 2: a real N+1 in
  StudioAiGeneratedProjects/MovieCollectionApp (`GetListAsync` maps each movie with a helper that runs
  `_genreRepository.FindAsync(movie.GenreId)`), and a false NP0004 in DocumentationSamples/CustomApplicationModules: an
  override of `GetAsync` that queries and returns `base.GetAsync(id)` was taken for recursion, because the base call
  was followed to every override. A non-virtual call now runs only the callee's own body (catalogue B29). eShop, both
  Clean Architecture templates and the four private applications report exactly what they did before.
- ~~**Base calls everywhere else.**~~ (done 2026-10-13, catalogue B30) The B29 fix covered only the recursion check: the
  search for a query, the lazy reads on arguments and the traced return value still followed `base.Method(...)` to every
  override. A base call in a loop was reported when only an override queried (also through a helper that makes the base
  call) or read a navigation, and a query returned by `base.Orders()` took the override's `Include` and missed the lazy
  load. Summaries now record whether each call is dispatched (`CallEdge.Dispatched`, from `IInvocationOperation.IsVirtual`),
  and `Session` looks up a call that is not dispatched in the callee's own body only. Method groups, delegates and
  property getters are still taken as dispatched (`base.Price` as a method group is rare). Corpus: `Reads/CallChains.cs`
  (`StoredPrices`, `FixedCounts`, `PriceList`) and `Lazy/BaseCalls.cs`. Counter-check `tools/countercheck/31-base-calls.json`.

## Not planned: limits of static analysis

- An implementation in a project the analyzing project does not reference, reflection and dynamic dispatch,
  and configuration decided at run time: the runtime guard covers these in tests.
- Owned types and `IgnoreAutoIncludes` corner cases beyond item 1: revisit if users report them.
- Dapper, raw ADO.NET, GraphQL resolvers (Hot Chocolate DataLoader): after v1, outside EF Core.
