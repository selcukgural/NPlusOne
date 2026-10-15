# N+1: runtime guard

The runtime guard watches the commands EF Core really runs inside a *scope* (a test, an HTTP
request, any block of code) and reports work that repeats once per element: the same query, the
same lazy-loaded navigation or, if you ask for it, the same write. It catches what the analyzer
cannot see, such as queries behind interfaces, reflection or a serializer walking lazy
navigations. See [design.md](design.md) for how the two layers fit together.

| Package | Targets | Use it for |
|---|---|---|
| `NPlusOne.EntityFrameworkCore` | net8.0 (EF Core 8), net9.0 (EF Core 9), net10.0 (EF Core 10) | Tests and manual scopes, any test framework, no DI needed |
| `NPlusOne.AspNetCore` | net8.0, net9.0, net10.0 | One scope per request, MVC and minimal API assert filters |

## Install

```sh
dotnet add package NPlusOne.EntityFrameworkCore
dotnet add package NPlusOne.AspNetCore   # web apps only
```

Attach the guard to every DbContext you want watched. It adds one shared interceptor and starts
listening for lazy loads; nothing is recorded until a scope begins.

```csharp
// OnConfiguring, a hand-built DbContextOptionsBuilder, or AddDbContext: all the same.
optionsBuilder.UseSqlite(connection).UseNPlusOneGuard();

services.AddDbContext<ShopContext>(o => o.UseSqlServer(connectionString).UseNPlusOneGuard());
```

## In tests

```csharp
using NPlusOne.EntityFrameworkCore;

[Fact]
public async Task Order_list_loads_customers_in_one_query()
{
    await using var db = CreateContext();                  // built with .UseNPlusOneGuard()
    using var guard = NPlusOneGuard.Begin();               // or Begin(o => o.RepeatThreshold = 3)

    await new OrderService(db).ListAsync();

    guard.AssertNoNPlusOne();                              // throws NPlusOneViolationException
}
```

`guard.Report` gives the same data programmatically: `Suspects` (each with `Kind`, `Count`,
`Sql`, `Navigation`, `CallSite`), `HasSuspects`, `CommandCount`, `LazyLoadCount` and
`RepeatThreshold`. The report stays available after the scope is disposed.

Scopes follow the async flow (`AsyncLocal`), so parallel tests never see each other's commands.
Scopes nest: while an inner scope is active, work is recorded in the inner scope *and* in every
enclosing one, each judged by its own options; disposing the inner scope makes the outer one
current again. Begin a scope in the method that runs the code (or a caller of it), not inside an
`async` helper that returns it: async-local values set inside an awaited method do not flow back
to its caller.

## In ASP.NET Core

```csharp
using NPlusOne.AspNetCore;
using NPlusOne.EntityFrameworkCore;

builder.Services.AddNPlusOneGuard(o =>
{
    o.ThrowOnViolation = builder.Environment.IsDevelopment();
    o.IgnoredSqlPatterns.Add(new Regex("FROM \"__EFMigrationsHistory\""));
});
builder.Services.AddDbContext<ShopContext>(o => o.UseSqlServer(cs).UseNPlusOneGuard());

var app = builder.Build();
app.UseNPlusOneGuard();                        // one scope per request

app.MapGet("/orders", (ShopContext db) => ...)
   .WithNPlusOneAssert();                      // minimal API: fail this endpoint (or a MapGroup)

[NPlusOneAssert]                               // MVC: fail this action or every action of the controller
public sealed class OrdersController : ControllerBase { ... }
```

**Middleware.** Place it after the exception handler (`UseExceptionHandler`, `UseDeveloperExceptionPage`) and before
routing and the endpoints, so a `ThrowOnViolation` exception becomes an error page. WebSocket requests (SignalR hubs,
Blazor Server circuits) are not watched: one stays open for a whole session, and a scope over it would count separate
actions as one request. Every other request runs in its own scope. When the request ends with suspects, the
middleware logs a warning (event `NPlusOneSuspects`) that names the HTTP method and the endpoint
(its route template or action, never the request path, which may carry ids or other values).
With `ThrowOnViolation`, it throws `NPlusOneViolationException` instead, but only while the
response has not started. Once headers and part of the body have been sent, an exception cannot
turn the response into an error any more; the server can only cut the connection, and the client
sees a truncated body. In that case the middleware logs an error (event
`NPlusOneViolationAfterResponseStarted`) instead. Most endpoints that return data have started
their response by the time the pipeline returns, so use the assert filters when a request must
fail.

**Assert filters.** `[NPlusOneAssert]` and `.WithNPlusOneAssert()` run the action or handler in
its own scope and check it right after it returns, before its result is written, so the
exception still becomes an error response. They throw whatever `ThrowOnViolation` says, in every environment but
Production: there they log the report as a warning (event `NPlusOneAssertInProduction`) and let the request succeed,
unless `AssertThrowsInProduction` is set. Whether an endpoint repeats a query depends on its data, so an assert that
throws in production lets anyone who can add rows turn the endpoint into an error page for every caller. They use the
other options registered with `AddNPlusOneGuard` (the defaults if it was not called).
They work with or without the middleware. Work done while the result is written, such as lazy
loads triggered by the JSON serializer, happens after their check; the middleware sees it. `[NPlusOneAssert]` is an MVC
action filter: it does nothing on a Razor Pages page model, and `.WithNPlusOneAssert()` applies to minimal API endpoints
and groups, not to `MapControllers()` or `MapRazorPages()`; use the middleware there.

The guard is opt-in: enable the middleware where you want it (development, staging, a share of
production traffic). With no scope active, the interceptor and the lazy-load listener cost one
async-local read per command and return. Inside a scope, a command is counted in a dictionary: its SQL is normalized
once per command text (EF Core sends the same text for every run of a query), a context's provider is looked up once,
and a scope that records nothing allocates a few hundred bytes. The call site is what costs: the stack is walked on
every command and lazy load (a few microseconds; the file and line of each code location are read once). In
`benchmarks/NPlusOne.Benchmarks`, a request with ten queries on SQLite took about 4% longer inside a scope, and about
80% longer with call sites; `CaptureCallSite = false` leaves them out.

## Options

| Option | Default | Meaning |
|---|---|---|
| `RepeatThreshold` | `2` | A group that runs **more** than this many times in one scope is a suspect. At least 1. |
| `ThrowOnViolation` | `false` | Middleware throws instead of logging a warning (see above). Manual scopes ignore it; call `AssertNoNPlusOne()`. |
| `AssertThrowsInProduction` | `false` | The assert filters throw in the Production environment too, instead of logging a warning (see above). |
| `IgnoredSqlPatterns` | empty | `Regex`es matched against the SQL as EF Core sends it; matching commands are not recorded. The SQL can carry inlined values, so a pattern without a timeout of its own stops after 100 ms on a command, and that command is then not recorded. |
| `IncludeWrites` | `false` | Also record `SaveChanges`, `ExecuteUpdate`, `ExecuteDelete` and `ExecuteSql` commands and the transactions the code starts, so a `SaveChanges` or a `BeginTransaction` per element is reported. One `SaveChanges` call counts once, however many statements it sends (and the transaction it opens is part of it). |
| `CaptureCallSite` | `true` | Group by call site as well as SQL, and show it. Off: group by SQL only, a little faster. |
| `MaxSqlPreviewLength` | `300` | Normalized SQL longer than this is cut and ends with `...`. At least 20. |

A scope copies the options when it begins; invalid values throw when they are set.

## How work is grouped

- **Queries and writes** are grouped by *fingerprint* and *call site*. The fingerprint is the SQL
  with string, number, GUID and hex literals replaced by `?`, parameter names by `@p`, comments
  and query tags removed, `IN (...)` lists of any length collapsed to `IN (?)`, and whitespace
  collapsed. The call site is the first stack frame outside EF Core, its providers, the .NET
  libraries, Castle proxies and the guard, shown as `Type.Method (File.cs:line)` when symbols are
  available. The same SQL from two different lines is two groups, so two independent lookups do
  not add up to a false suspect.
- **Lazy loads** are counted through EF Core's `CoreEventId.NavigationLazyLoading` event, for the contexts built with
  `UseNPlusOneGuard` only, and grouped by `Entity.Navigation` and call site. A lazy load is also a query, so a lazy-loading
  loop shows up twice: once as the navigation (which tells you what to `Include`) and once as its
  SQL.
- Migrations and value-generator round trips (sequences, HiLo) are never recorded.

## What a report looks like

```text
N+1 guard found 2 suspect(s): the same work ran more than 2 times in one scope (11 commands, 5 lazy loads recorded).
  - Query ran 5 times at OrderService.ListAsync (OrderService.cs:42)
    SELECT "o"."Id", "o"."CustomerId", "o"."Number" FROM "Orders" AS "o" WHERE "o"."CustomerId" = @p
  - Lazy load of Customer.Orders ran 5 times at OrderService.ListAsync (OrderService.cs:42)
Load the data once for all elements instead: Include, a projection, or one query with Contains followed by a lookup.
```

Reports never contain parameter values or literals: the SQL shown is always the fingerprint,
never the command text. A report is safe to log and to put into an exception message. Dialects disagree on what a
backslash and a double quote mean, and a literal read the wrong way would show the text after it, so the fingerprint
reads each command in its provider's dialect (SQL Server, SQLite, PostgreSQL, MySQL, Oracle), under every server
setting that changes the reading (MySQL's `NO_BACKSLASH_ESCAPES`, PostgreSQL's `standard_conforming_strings`), and
hides what any of these readings takes for a value. Double-quoted names stay readable on PostgreSQL, SQLite and Oracle;
on MySQL and SQL Server a double-quoted run is a value. Other providers get every reading. Dollar quotes (`$$...$$`) and
Oracle's `q'[...]'` are literals and block comments nest everywhere. One gap is left: SQLite's legacy reading of a
double-quoted word that names no column as a string (hand-written SQL only) shows the word, as it must for the names
EF Core quotes. The guard only observes: if recording fails (an ignored pattern that times out, say), that
one record is dropped and the application's command runs as usual.

## Limitations

- **Only what runs.** Code paths that no test or request executes are invisible; the analyzer
  covers those.
- **Async connection opens can hide the call site.** EF Core opens the connection before it runs
  a reader command. If the open completes asynchronously on another thread (a cold connection
  pool, for example), the application frames are no longer on the stack; the command is then
  grouped by fingerprint alone and reported without a call site. SQLite and warm pools open
  synchronously.
- **Call sites are frames, not lines of intent.** A loop in a shared helper is reported at the
  helper. Lazy loads through an injected `ILazyLoader` skip the entity's property getter and
  report its caller.
- **EF Core only.** Dapper, raw ADO.NET and HTTP calls are not seen.
- **One process.** Scopes follow the async flow; work handed to another flow that does not
  inherit the execution context (a queue, a background service) is not recorded in the
  request's scope.
