# N+1 catalogue

Every way EF Core code ends up running one round trip (query, lazy load, save) per element, what the analyzer must
do about it, and where it stands. Each row is a case to check, test (a corpus case on both sides) and fix. Built on
2026-10-12 against the analyzer at `1a3749d` plus item 6's first batch of corpus cases; probes compiled with the
real MediatR 12.5, AutoMapper 14, Mapster 7.4 and Mediator 3.0 packages, EF Core 10, lazy loading on.

Every corpus case is also run against SQLite by `GroundTruthTests` (see `docs/design.md`); a row marked covered is
proven at run time unless its corpus method says otherwise in a `// ground-truth:` comment.

Status:

- **covered**: a corpus case guards it (file named).
- **works**: a probe showed the right result, but no corpus case guards it yet; add one.
- **gap: missed** / **gap: false positive**: the probe showed the wrong result; fix direction given.
- **runtime guard**: static analysis cannot know it; the runtime guard catches it in tests.
- **not N+1**: a constant number of round trips, or not a database round trip; listed so nobody re-checks it.
- **unverified**: needs a package or provider the harness does not reference yet.

## A. EF Core APIs

| # | Case | Snippet | Expected | Status | Fix direction |
|---|---|---|---|---|---|
| A1 | Executing operator in a loop | `foreach (c in cs) await db.Orders.Where(...).ToListAsync()` | NP0001 | covered: `Reads/Loops.cs` | |
| A2 | Less common sync operators | `db.Orders.All(...)`, `Contains`, `LongCount`, `Max`, `Average`, `LastOrDefault`, `ElementAtOrDefault`, `ToArray` in a loop | NP0001 | covered: `Reads/WorksCases.cs` | |
| A3 | Less common async operators | `AllAsync`, `ContainsAsync`, `LongCountAsync`, `MaxAsync`, `ToDictionaryAsync`, `ToHashSetAsync`, `SingleOrDefaultAsync`, `ToArrayAsync` | NP0001 | covered: `Reads/WorksCases.cs` | |
| A4 | Find by key in a loop | `await db.Orders.FindAsync(id)` | NP0001 | covered: `Reads/Loops.cs` | |
| A5 | Set-based statements per element | `db.Orders.Where(o => o.Id == id).ExecuteDeleteAsync()` / `ExecuteUpdateAsync` | NP0001 | covered: `Reads/WorksCases.cs` | |
| A6 | Raw SQL per element | `Database.ExecuteSqlAsync($"...")`, `FromSqlInterpolated` | NP0001 | covered: `Reads/Loops.cs` | |
| A7 | Other raw SQL APIs | `Database.SqlQuery<int>(...)`, `ExecuteSqlRawAsync`, `FromSqlRaw` | NP0001 | covered: `Reads/WorksCases.cs` | |
| A8 | Explicit loading per element | `db.Entry(o).Reference(x => x.Customer).LoadAsync()`, `.Collection(...).Query().LoadAsync()` | NP0001 | covered: `Reads/Loops.cs` | |
| A9 | Other entry round trips | `Entry(o).Collection(...).Query().CountAsync()`, `ReloadAsync()`, `GetDatabaseValuesAsync()` | NP0001 | covered: `Reads/WorksCases.cs` | |
| A10 | `IsLoaded`, `State =` per element | `db.Entry(o).Collection(x => x.Lines).IsLoaded` | silent | covered: `Reads/WorksCases.cs` | |
| A11 | Lazy loading with proxies | `foreach (o in orders) o.Customer.Name` | NP0002 | covered: `Lazy/LazyNavigation.cs` | |
| A12 | Lazy loading with `ILazyLoader` / `Action<object, string>`; plain auto-property navigations of such entities | `get => LazyLoader.Load(this, ref _posts)` | NP0002 / silent | covered: `LazyLoader/LazyLoaderEntities.cs` | |
| A13 | Include, ThenInclude, string Include | `Include(o => o.Customer)`, `Include("Customer")` | silent | covered: `Lazy/LazyNavigation.cs` | |
| A14 | Filtered Include | `Include(o => o.Lines.Where(l => l.Price > 0))` | silent | covered: `Lazy/WorksLazy.cs` | |
| A15 | Second level not included | `Include(c => c.Orders)` then `o.Lines` per order | NP0002 | covered: `Lazy/LazyNavigation.cs` | |
| A16 | `AutoInclude()` / `IgnoreAutoIncludes()` | `b.Navigation(x => x.Customer).AutoInclude()` | silent / NP0002 | covered: `Lazy/LoadedWithoutInclude.cs` | |
| A17 | Untracked results, lazy loading turned off | `AsNoTracking()`, `AsNoTrackingWithIdentityResolution()`, `LazyLoadingEnabled = false` | EF Core 8+: NP0002 (a shadow-FK reference throws: silent); EF Core ≤7 and `LazyLoadingEnabled = false`: silent | covered: `Lazy/LoadedWithoutInclude.cs`, `Lazy/ComputedAndPassed.cs` (shadow FK), run-time proven | |
| A17b | Collection back-reference under an included reference, serialized | `db.Orders.Include(o => o.Customer)` returned; `Customer.Orders` is not loaded | NP0005 (a reference back-reference: silent) | covered: `Endpoints/HubsAndViews.cs`, run-time proven | |
| A18 | Split queries | `Include(...).ThenInclude(...).AsSplitQuery()` | silent | covered: `Lazy/WorksLazy.cs` | |
| A19 | Compiled queries invoked per element | `static readonly Func<AppDb, int, Task<Order>> ById = EF.CompileAsyncQuery(...)`; `await ById(db, id)` in a loop | NP0001 | covered: `Reads/Accessors.cs` | |
| A20 | `DbSet.Local` lookups | `db.Orders.Local.FirstOrDefault(...)` | silent | covered: `Reads/WorksCases.cs` | |
| A21 | Add/Update/Remove/Attach without saving | `db.Orders.Add(o)` per element | silent | covered: `Writes/WorksWrites.cs` | |
| A22 | SaveChanges per element | `await db.SaveChangesAsync()` in a loop, lambda or helper | NP0003 | covered: `Writes/SaveChangesPerElement.cs` | |
| A23 | Transaction per element | `await using var tx = await db.Database.BeginTransactionAsync(); ... await tx.CommitAsync();` in a loop | NP0003 | covered: `Writes/Transactions.cs` | |
| A24 | Correlated subquery or collection in a projection | `db.Customers.Select(c => new { c.Name, Orders = c.Orders.ToList() })` | silent | covered: `Reads/NotNPlusOne.cs` | |
| A25 | Client-evaluated final projection calling a method that reads navigations | `db.Orders.Select(o => Describe(o)).ToListAsync()`, `Describe` reads `o.Customer` | NP0002 | covered: `Lazy/ClientProjection.cs` | |
| A26 | Client-evaluated final projection calling a service that queries | `db.Customers.Select(c => new { c.Id, N = counter.Count(c.Id) })` (also on the same context: SQLite runs it) | NP0001 | covered: `Reads/ClientProjection.cs` | |
| A27 | A new context per element | `using var ctx = new AppDb(...)` / `await factory.CreateDbContextAsync()` in a loop | NP0001 | covered: `Reads/Loops.cs` (factory: works) | |
| A28 | Query built but not executed in a loop | `var q = db.Orders.Where(...)` | silent | covered: `Reads/NotNPlusOne.cs` | |
| A29 | Query captured outside, executed per element | `var q = db.Orders.Where(...)`; `q.Count(...)` in the loop | NP0001 | covered: `Reads/Loops.cs` | |
| A30 | Query typed as `IEnumerable<T>`, enumerated per element | `IEnumerable<Order> big = db.Orders.Where(...)`; `big.Count(...)` in the loop | NP0001 | covered: `Reads/WorksCases.cs` | |
| A31 | `AsEnumerable()` in a loop | `db.Orders.AsEnumerable().Where(...).ToList()` | NP0001 | covered: `Reads/WorksCases.cs` | |
| A32 | `Load()` on a query per element | `await db.Orders.Where(...).LoadAsync()` | NP0001 | covered: `Reads/CallChains.cs` | |
| A33 | `await foreach` over a query inside a loop | `await foreach (var o in db.Orders.Where(...).AsAsyncEnumerable())` | NP0001 | covered: `Reads/Loops.cs` | |
| A34 | `foreach` directly over a query, reading lazy navigations | `foreach (var o in db.Orders) o.Customer.Name` | NP0002 | covered: `Lazy/LazyNavigation.cs` | |
| A35 | Owned types (`OwnsOne`, `OwnsMany`) | `s.Origin.City` per element | silent | covered: `Lazy/WorksLazy.cs` | |
| A36 | Many-to-many skip navigations | `s.Tags.Count` per element | NP0002 | covered: `Lazy/WorksLazy.cs` | |
| A37 | Navigations of derived types (TPH/TPT) when only the base type has a `DbSet` | `db.Parties.OfType<Company>()`; `c.Sector.Label` per element | NP0002 | covered: `Lazy/DerivedTypes.cs` | |
| A38 | Query filters, tags, temporal operators | `IgnoreQueryFilters().TagWith(...)`, `TemporalAsOf(...)` in a loop | NP0001 | covered: `Reads/WorksCases.cs` (filters and tags; temporal operators need the SQL Server provider and are not run) | |
| A39 | Paging, polling, `Chunk`, search-until-unique loops | `while (...) { ...Skip(n).Take(100)... }` | silent | covered: `Reads/NotNPlusOne.cs` | |
| A40 | Load-once guards | `x ??= await query` | silent | covered: `Reads/NotNPlusOne.cs` | |
| A41 | Lazy collection counted per parent | `c.Orders.Count` / `c.Orders.Any()` | NP0002 | covered: `Lazy/LazyNavigation.cs` | |
| A42 | Client-side `GroupBy` (EF Core 2 client evaluation) | `db.Orders.GroupBy(...).Select(g => g.ToList())` | silent | not N+1 (EF Core 3+ throws instead) | |
| A43 | Cartesian explosion of several collection Includes | `Include(c => c.Orders).Include(c => c.Addresses)` | silent | not N+1 (one large query; `AsSplitQuery` is the fix) | |
| A44 | Retries, timer polls, draining in pages, concurrency conflict handling | `while (!saved) { try { db.SaveChanges(); saved = true; } catch (DbUpdateConcurrencyException e) { foreach (var entry in e.Entries) ... } }`; a `try` around each element's save stays NP0003 | silent / NP0003 | covered: `Writes/RetriesAndDrains.cs` | |

## B. C# and .NET constructs

| # | Case | Snippet | Expected | Status | Fix direction |
|---|---|---|---|---|---|
| B1 | `for`, `foreach`, `while`, `do`, `await foreach` | query in the body | NP0001 | covered: `Reads/Loops.cs` | |
| B2 | LINQ to Objects lambdas | `Select`, `Where`, `ToDictionary`, `SelectMany`, `List.ForEach`, `Array.ForEach` | NP0001 | covered: `Reads/Lambdas.cs` | |
| B3 | More LINQ lambdas | `Sum`, `OrderBy`, `Any`, `Aggregate`, `ToLookup` element selector, `Max`, `string.Join(",", cs.Select(...))` | NP0001 | covered: `Reads/WorksCases.cs` | |
| B4 | Parallel and concurrent | `Parallel.For/ForEach/ForEachAsync`, `Task.WhenAll(ids.Select(...))`, `Task.Run` in a loop | NP0001 | covered: `Reads/Lambdas.cs` | |
| B5 | Query syntax | `from c in cs let n = db.Orders.Count(...) select ...`, `select c.Address.City` | NP0001 / NP0002 | covered: `Reads/WorksCases.cs`, `Lazy/WorksLazy.cs` | |
| B6 | Iterators, local functions | `yield return await db...`, local function called in a loop | NP0001 | covered: `Reads/Lambdas.cs` | |
| B7 | Delegate in a local | `Func<int, Task<int>> count = id => db...`; `await count(id)` in a loop | NP0001 | covered: `Reads/StoredAndShared.cs` | |
| B8 | Delegate in a field | `private readonly Func<int, int> _count = id => db.Orders.Count(...)`; `_count(id)` in a loop | NP0001 | covered: `Reads/Accessors.cs` | |
| B9 | Direct and mutual recursion | method queries and calls itself / another method that calls it | NP0004 | covered: `Reads/CallChains.cs`, `Reads/StoredAndShared.cs` | |
| B10 | Helpers, services, interface and virtual dispatch | `await helper.Load(id)` in a loop | NP0001 | covered: `Reads/CallChains.cs`, `Reads/Strategies.cs` | |
| B11 | Extension methods | `c.OrderCount(db)` | NP0001 | covered: `Reads/WorksCases.cs` | |
| B12 | Generic methods | `CountOf<Order>()` → `db.Set<T>().Count()` | NP0001 | covered: `Reads/WorksCases.cs` | |
| B13 | Default interface methods | `counter.CountTwice(id)` calling an implemented member | NP0001 | covered: `Reads/WorksCases.cs` | |
| B14 | Property getter (not an entity) that queries | `row.OrderCount` → `db.Orders.Count(...)` | NP0001 (an assignment runs the setter only: silent) | covered: `Reads/Accessors.cs` | |
| B15 | Indexer that queries | `lookup[id]` → `db.Orders.First(...)` | NP0001 | covered: `Reads/Accessors.cs` | |
| B16 | User-defined conversion reading navigations | `OrderLine2 line = o;` with `implicit operator` reading `o.Customer.Name` | NP0002 | covered: `Lazy/ImplicitReads.cs` | |
| B17 | Property patterns | `orders.Where(o => o is { Customer.Name: "vip" })`, nested `{ Desk: { Floor: { ... } } }`, `switch` | NP0002 | covered: `Lazy/ImplicitReads.cs` | |
| B18 | Null-conditional and `switch` reads | `o.Customer?.Address?.City` | NP0002 | covered: `Lazy/WorksLazy.cs` | |
| B19 | Walking a lazy reference chain | `while (c.Parent != null) c = c.Parent;` | NP0002 | covered: `Lazy/ImplicitReads.cs` (the ground truth cannot drive the depth: the seeder builds two-level trees) | |
| B20 | `foreach` with deconstruction, tuple items, key/value pairs | `foreach (var (id, o) in orders.Select(o => (o.Id, o)))`, `pair.Order`, `foreach (var (id, o) in byId)`, `kv.Value`, `orders.Index()` | NP0002 (silent when the part is an entity shared by every element) | covered: `Lazy/IndexedAndDeconstructed.cs` | |
| B21 | `for` loop over an EF list or array by index | `for (i...) orders[i].Customer.Name`, backwards, through a local | NP0002 (silent for an index that does not use the stepped counter) | covered: `Lazy/IndexedAndDeconstructed.cs` | |
| B22 | `ToString()` override reading navigations | `$"{desk}"`, `Console.WriteLine(desk)`, `string.Join(", ", desks)`, `StringBuilder.Append`, `TextWriter.Write` | NP0002 | covered: `Lazy/ImplicitReads.cs` | |
| B23 | Record entity printed or compared | `public record Ticket { virtual Agent Agent }`; `t.ToString()` reads every navigation | NP0002 when printed; silent when compared (the generated `Equals`/`GetHashCode` compare fields, measured) | covered: `Lazy/ImplicitReads.cs` | |
| B24 | `Equals`/`GetHashCode` overrides reading navigations | `orders.Distinct()`, `new HashSet<Order>(orders)`, `ToHashSet`, `Union`, `Intersect`, `Except` | NP0002 | covered: `Lazy/ImplicitReads.cs` | |
| B25 | Events raised per element | `Processed?.Invoke(id)` with a querying subscriber | NP0001 | runtime guard (subscribers are decided at run time) | |
| B26 | Elements passed to helpers, computed members | `PrintCustomers(orders)`, `invoices.Sum(i => i.Total)` | NP0002 | covered: `Lazy/ComputedAndPassed.cs` | |
| B27 | Query fields and properties set in the constructor | `_big = db.Orders.Where(...)` | NP0001 | covered: `Reads/StoredAndShared.cs` | |
| B28 | Element parts B20 does not follow | `pairs.Sum(p => p.Order.Customer.Name.Length)` (a part in a lambda), `Zip`, pairs built with `new KeyValuePair(...)`, records built in `Select` (deconstructed or read by property), an index stepped inside a `while` loop | NP0002 | covered: `Lazy/MoreElementParts.cs` | |

## C. ASP.NET Core and API layer

| # | Case | Snippet | Expected | Status | Fix direction |
|---|---|---|---|---|---|
| C1 | Controller returns entities | `Task<List<Order>>`, `ActionResult<T>`, `Ok(orders)`, single entity | NP0005 | covered: `Endpoints/Endpoints.cs` | |
| C2 | `CreatedAtAction`, `Created`, `Accepted` | `return CreatedAtAction(nameof(Get), new { id }, order)` | NP0005 | covered: `Endpoints/WorksEndpoints.cs` | |
| C3 | `Json(...)`, `new OkObjectResult(...)`, `new JsonResult(...)` | `return Json(orders)` | NP0005 | covered: `Endpoints/HubsAndViews.cs` | |
| C4 | `IAsyncEnumerable<T>` result | `=> db.Orders.AsAsyncEnumerable()` | NP0005 | covered: `Endpoints/HubsAndViews.cs`, `Reads/Loops.cs` (`DbSet.AsAsyncEnumerable()`) | |
| C5 | `IQueryable<T>` result (the serializer enumerates it) | `public IQueryable<Order> Get() => db.Orders` | NP0005 | covered: `Endpoints/WorksEndpoints.cs` | |
| C6 | Entities returned through a service | `=> await service.AllAsync()` | NP0005 | covered: `Endpoints/WorksEndpoints.cs` | |
| C7 | Entities inside a result container | `new Paged<Order>(await db.Orders.ToListAsync(), total)`, `new Paged<Order> { Items = orders }` | NP0005 | covered: `Endpoints/Containers.cs` | |
| C8 | Entities inside an anonymous object | `Ok(new { items = await db.Orders.ToListAsync() })` | NP0005 | covered: `Endpoints/Containers.cs` | |
| C9 | Navigations marked `[JsonIgnore]` | `[JsonIgnore] public virtual List<Book> Books` | silent (NP0005 for `WhenWritingNull`/`WhenWritingDefault`/`WhenWriting`, which read the value, and for the other serializer's attribute) | covered: `Endpoints/JsonIgnored.cs` | |
| C10 | Minimal API lambda | `MapGet(..., async db => await db.Customers.ToListAsync())`, `TypedResults.Ok`, `Results.Ok`, block bodies | NP0005 | covered: `Endpoints/Endpoints.cs` (`Results.Ok`, blocks: works) | |
| C11 | `Results.Json(data)`, `TypedResults.Json`, `Json(data, options)` | `Results.Json(await db.Orders.ToListAsync())` | NP0005 | covered: `Endpoints/Endpoints.cs`, `Endpoints/HubsAndViews.cs` | |
| C12 | Minimal API method groups, `MapGroup` | `api.MapGet("/orders", GetOrders)` | NP0005 | covered: `Endpoints/HubsAndViews.cs` | |
| C13 | Query per element inside a handler | `foreach (id in ids) await db.Orders.FindAsync(id)` | NP0001 | covered: `Endpoints/WorksEndpoints.cs` | |
| C14 | `View(...)`, `PartialView(...)` | `return View(await db.Orders.ToListAsync())` | silent | covered: `Endpoints/HubsAndViews.cs` | |
| C15 | Razor views, Pages, view components, tag helpers reading navigations in markup | `@foreach (var o in Model) { @o.Customer.Name }` | NP0002 | runtime guard in the corpus; real builds include the Razor generator's output, so check with `tools/scan.sh` on an MVC app | Scan an MVC/Razor Pages app; follow `Model` from the `View(...)` call into the generated page if feasible |
| C16 | Blazor components | markup loops; a row component querying in `OnInitializedAsync` | NP0001 / NP0002 | runtime guard (the component tree is built at render time) | |
| C17 | SignalR `SendAsync` | `_hub.Clients.All.SendAsync("orders", orders)` | NP0005 | covered: `Endpoints/Serialization.cs` | |
| C18 | Strongly typed hub clients, hub method results | `Clients.Caller.OrdersChanged(orders)`, `Task<List<Order>> GetOrders()` | NP0005 | covered: `Endpoints/HubsAndViews.cs` | |
| C19 | Streaming hub methods | `IAsyncEnumerable<Order>` returned or yielded, `ChannelReader<Order>` whose channel the method writes | NP0005 | covered: `Endpoints/StreamingHub.cs` (a writer passed to another method is not followed) | |
| C20 | Explicit serialization | `JsonSerializer.Serialize(orders)`, `SerializeAsync` | NP0005 | covered: `Endpoints/Serialization.cs` | |
| C21 | Newtonsoft | `JsonConvert.SerializeObject(orders)`; skips its own `[JsonIgnore]` and `[IgnoreDataMember]` | NP0005 | covered: `Endpoints/Serialization.cs`, `Endpoints/JsonIgnored.cs` | |
| C22 | gRPC | `reply.Orders.AddRange(orders.Select(o => new OrderMsg { Customer = o.Customer.Name }))` | NP0002 | covered by the generic rule: `Lazy/LazyNavigation.cs` | |
| C23 | OData `[EnableQuery]` returning `IQueryable` | `[EnableQuery] public IQueryable<Order> Get() => db.Orders` (on the action or its controller) | silent | covered: `Endpoints/OData.cs` (the harness cannot run OData's formatter: those cases say why) | |
| C24 | GraphQL resolvers per parent without a DataLoader | `public Task<Customer> GetCustomer([Parent] Order o, AppDb db) => db.Customers.FindAsync(o.CustomerId)` | NP0001 | runtime guard (not planned before v1, see backlog) | Later: treat Hot Chocolate type-extension resolvers taking `[Parent]` as per-element |
| C25 | Middleware, filters, health checks, hosted-service polling | one query per request or per poll | silent | covered: `Reads/NotNPlusOne.cs`, `Reads/Strategies.cs` | |
| C26 | Validation attributes, `IValidatableObject` querying per bound item | `[ExistingProduct] public List<int> ProductIds` | NP0001 | runtime guard (the framework invokes them by reflection) | |
| C27 | Razor `PageModel` property assigned from a query | `Orders = await db.Orders.ToListAsync()` in `OnGetAsync` | silent (the markup decides, C15) | covered: `Endpoints/WorksEndpoints.cs` | |
| C28 | MVC switched to Newtonsoft, navigations skipped with Newtonsoft's attributes | `AddNewtonsoftJson()`; `[Newtonsoft.Json.JsonIgnore] public virtual Customer Customer` on a returned entity | silent (minimal APIs stay System.Text.Json) | covered: `EndpointsNewtonsoft/NewtonsoftMvc.cs` | |

## D. Libraries

| # | Case | Snippet | Expected | Status | Fix direction |
|---|---|---|---|---|---|
| D1 | MediatR `Send`/`Publish` per element, also through helpers | `await sender.Send(new GetCustomerTotal(id))` | NP0001 | covered: `Reads/Mediator.cs` | |
| D2 | MediatR requests without a response, inherited `Handle`, `Send(object)` (through an `object` local) | `record ArchiveOrder(int Id) : IRequest` | NP0001 / silent | covered: `Reads/MediatRVariants.cs` | |
| D3 | Mediator (source-generated): `ICommand`, `IQuery`, `ValueTask` handlers | `await mediator.Send(new GetOrderCount(id))` | NP0001 | covered: `Reads/MediatorLibrary.cs` | |
| D4 | MediatR pipeline behaviors that query | `class AuditBehavior<TReq, TRes>(AppDb db) : IPipelineBehavior<TReq, TRes>` querying; `Send` in a loop | NP0001 when the behavior is registered (`typeof`, `AddBehavior`, `AddOpenBehavior`) and its constraints accept the request; silent otherwise | covered: `Reads/MediatRPipeline.cs` (MediatR; the Mediator library's behaviors are not followed) | |
| D5 | MediatR stream requests | `await foreach (var o in mediator.CreateStream(new StreamOrders(id)))` in a loop | NP0001 | covered: `Reads/MediatRPipeline.cs` | |
| D6 | AutoMapper `Map` of a result or of each element; profiles, `ForMember`/`MapFrom`/`Ignore` | `mapper.Map<List<OrderView>>(orders)` | NP0002 / NP0007 / silent | covered: `Mapping/MappingCases.cs` | |
| D7 | AutoMapper `Map<S, D>`, `Map(src, dest)`, inline `MapperConfiguration`, `ReverseMap`, `ForMember` after other chained calls | `CreateMap<Order, OrderRow>().AfterMap(...).ForMember(d => d.CustomerName, o => o.Ignore())` | NP0002 / silent | covered: `Mapping/RealApis.cs` | |
| D8 | `ProjectTo` (extension and `IMapper.ProjectTo`) | `db.Orders.ProjectTo<OrderView>(config)` | silent | covered: `Mapping/MappingCases.cs`, `Mapping/RealApis.cs` | |
| D9 | AutoMapper `ForPath` | `ForPath(d => d.Buyer.Name, o => o.MapFrom(s => s.Customer.Name))`; `ForPath(..., o => o.Ignore())` drops the root member's convention (measured) | NP0002 / silent | covered: `Mapping/AutoMapperMembers.cs` | |
| D10 | AutoMapper `IncludeMembers` | `CreateMap<Order, OrderNameView>().IncludeMembers(s => s.Customer)` | NP0002 | covered: `Mapping/AutoMapperMembers.cs` | |
| D11 | AutoMapper value resolvers and converters | `o.MapFrom<BuyerResolver>()` reading `source.Customer`; a resolver querying `db`; `ConvertUsing<T>()` / `ConvertUsing(s => ...)` | NP0002 / NP0001 | covered: `Mapping/AutoMapperMembers.cs` | |
| D12 | `Map` as a method group | `orders.Select(mapper.Map<PathView>).ToList()` | NP0002 | covered: `Mapping/AutoMapperMembers.cs` | |
| D13 | Mapster `Adapt` | `orders.Adapt<List<OrderView>>()` | NP0007 | covered: `Mapping/MappingCases.cs` | |
| D14 | Mapster `IMapper`, `TypeAdapterConfig`, `ProjectToType` | `mapsterMapper.Map<List<OrderView>>(orders)` | NP0007 / NP0002 / silent | covered: `Mapping/RealApis.cs` | |
| D15 | Mapperly (source-generated mapper methods) | `[Mapper] partial class M { partial OrderView ToView(Order o); }` | NP0002 | covered: `Mapping/Mapperly.cs` (the corpus compilation runs Mapperly's generator) | |
| D16 | FluentValidation `RuleForEach(...).MustAsync` querying | `RuleForEach(x => x.ProductIds).MustAsync(async (id, ct) => await db.Products.AnyAsync(...))` | NP0001 (silent on `RuleFor`) | covered: `Reads/Validation.cs` | |
| D17 | Generic repository with an in-project implementation | `IRepository<T>.GetByIdAsync(id)` → `db.Set<T>().FindAsync(id)` | NP0001 | covered: `Reads/WorksCases.cs` | |
| D18 | Unit of work wrapping `SaveChanges` | `await uow.CommitAsync()` in a loop | NP0003 | covered: `Writes/WorksWrites.cs` | |
| D19 | Repositories and specifications from a package (Ardalis.Specification) | `await repository.ListAsync(spec)` in a loop | NP0006 | covered by the rule: `Reads/CallChains.cs` (`InterfaceWithoutImplementation`) | |
| D20 | Implementations in referenced projects | interface implemented in the infrastructure project | NP0001 | covered: `CrossProject/` | |
| D21 | Caching wrappers | `cache.GetOrCreateAsync(key, _ => db...)` in a loop | silent | covered: `Reads/WorksCases.cs` (was a false positive, fixed 2026-10-12; a key per element is reported: the cache misses once per element) | |
| D22 | Logging entities | Serilog `logger.Information("{@Order}", o)` destructures every navigation | NP0002 | runtime guard (the sink's destructuring decides) | |
| D23 | Background jobs enqueued per element | `BackgroundJob.Enqueue(() => Process(id))` | silent | not N+1 (job storage writes, not EF) | |

## E. Reflection, dynamic dispatch, generated code

| # | Case | Snippet | Expected | Status | Fix direction |
|---|---|---|---|---|---|
| E1 | Service resolved per element, then called | `services.GetRequiredService<IOrderCounter>().Count(id)` | NP0001 | covered: `Reads/WorksCases.cs` | |
| E2 | `Activator.CreateInstance` cast to an interface | `((IOrderCounter)Activator.CreateInstance(...)).Count(id)` | NP0001 | covered: `Reads/WorksCases.cs` | |
| E3 | Expression trees built at run time | `db.Orders.Where(Expression.Lambda<Func<Order, bool>>(body, p)).ToList()` in a loop | NP0001 | covered: `Reads/WorksCases.cs` | |
| E4 | `MethodInfo.Invoke` | `method.Invoke(target, [id])` | NP0001 | runtime guard | |
| E5 | `dynamic` | `dynamic t = ...; t.Count(id)` | NP0001 | runtime guard | |
| E6 | Delegates injected from DI | `Func<int, int> injected`; `injected(id)` | NP0001 | runtime guard | |
| E7 | C# interceptors | a call redirected at compile time to a querying method | NP0001 | runtime guard (the analyzer sees the original target) | |
| E8 | String-based `Include`, `EF.Property` | `Include("Customer")`, `EF.Property<string>(o, "Note")` | silent | covered: `Lazy/WorksLazy.cs`, `Reads/WorksCases.cs` | |

## Summary

| Status | Count |
|---|---|
| covered | 119 |
| works (needs a corpus case) | 0 |
| gap: missed | 0 |
| gap: false positive | 0 |
| runtime guard | 10 |
| not N+1 | 3 |
| unverified | 0 (A38's temporal operators stay unrun: SQL Server provider) |
| **total** | **132** |

Some rows count once under their main status even when part of the row is covered elsewhere.

## Order for the gaps

Every row closed from here gets its run-time proof with it: its corpus cases run in `GroundTruthTests`.

Precision first, then the patterns most common in real code, then the rare ones:

1. ~~**C9** `[JsonIgnore]` navigations~~ (done 2026-10-12, with C21), ~~**C23** OData `[EnableQuery]`, **C28** Newtonsoft MVC
   endpoints~~ (done 2026-10-12).
2. ~~**B21** `for` loop by index, **B20** `foreach` deconstruction~~ (done 2026-10-12; the rarer shapes are B28).
3. ~~**C11** `Results.Json`, **C7/C8** entities inside containers and anonymous objects~~ (done 2026-10-12).
4. ~~**D11** AutoMapper resolvers/converters, **D10** `IncludeMembers`, **D12** `Map` as a method group, **D9** `ForPath`
   read explicitly~~ (done 2026-10-12).
5. ~~**A25/A26** client-evaluated final projections~~ (done 2026-10-12).
6. ~~**D4** MediatR pipeline behaviors, **D5** stream requests, **C19** streaming hub methods~~ (done 2026-10-12).
7. ~~**D16** FluentValidation `RuleForEach`~~ (done 2026-10-12).
8. ~~**B14/B15** property and indexer getters that query, **B8/A19** delegate fields and compiled queries~~ (done
   2026-10-12).
9. ~~**A37** derived-type navigations~~ (done 2026-10-12).
10. ~~**B22/B23/B24** `ToString`, record printers and equality reading navigations; **B16** conversion operators;
    **B17** property patterns; **B19** lazy reference chains~~ (done 2026-10-12).
11. ~~**A23** transaction per element~~ (done 2026-10-12).

~~Then turn every **works** row into a corpus case (both sides), and close the **unverified** rows by referencing the
packages (Riok.Mapperly, Microsoft.AspNetCore.OData) in the test project.~~ (done 2026-10-12: every row is covered, a
runtime-guard row, or not N+1.)
