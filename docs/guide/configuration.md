---
sidebar_position: 4
---

# Configuration

Both layers work without configuration. This page lists everything you can change.

## Analyzer severities

Every rule's severity is set the usual way, in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.NP0001.severity = error      # fail the build
dotnet_diagnostic.NP0006.severity = warning    # raise a suggestion
dotnet_diagnostic.NP0007.severity = none       # turn a rule off
```

## Analyzer options

Options go in a `.globalconfig` file next to the project or solution (or in the `[*.cs]` section of `.editorconfig`):

```ini
is_global = true
nplusone.lazy_loading = enabled
nplusone.query_methods = ICatalogClient.Get*;LegacyDb.Lookup
nplusone.ignored_methods = IPriceCache.*
```

| Key | Default | Meaning |
|---|---|---|
| `nplusone.lazy_loading` | `auto` | `enabled`, `disabled`, or `auto`: on when the project references the EF Core proxies package or an entity takes an `ILazyLoader`. |
| `nplusone.query_methods` | | Methods that query on every call, for abstractions whose implementation the analyzer cannot see (a client of another service, a package). Reported as NP0001. |
| `nplusone.ignored_methods` | | Methods that never make a round trip, even if they look like they do (a cached lookup). |
| `nplusone.iterating_methods` | | Methods that call the delegate they are given once per element, like `ForEach`. |
| `nplusone.data_access_types` | `*Repository;*Repo;*Store;*Dao;...` | Type names NP0006 treats as data access when their implementation is not visible. |
| `nplusone.max_call_depth` | `4` | How many calls deep a round trip is followed (0 to 16). |

Method patterns are separated by `;` and match `Type.Method` by simple name; `*` matches any run of characters.

| Project property | Default | Meaning |
|---|---|---|
| `NPlusOneEmitSummaries` | `true` | Write the project's method summaries into its assembly, so projects that reference it see through its calls. |

## Runtime guard options

Passed to `NPlusOneGuard.Begin(o => ...)` for a manual scope, or to `AddNPlusOneGuard(o => ...)` in ASP.NET Core:

| Option | Default | Meaning |
|---|---|---|
| `RepeatThreshold` | `2` | A group (same normalized SQL, or same lazy-loaded navigation, from the same call site) that runs more often than this in one scope is a suspect. |
| `IncludeWrites` | `false` | Also report `SaveChanges`, `ExecuteUpdate`, `ExecuteDelete`, `ExecuteSql` and transactions started per element. One `SaveChanges` counts once however many rows it writes. |
| `IgnoredSqlPatterns` | empty | Regular expressions; commands whose SQL matches are not recorded. |
| `CaptureCallSite` | `true` | Group by call site as well as SQL, and show where to look. |
| `ThrowOnViolation` | `false` | ASP.NET Core middleware: throw instead of logging, while the response has not started. |
| `MaxSqlPreviewLength` | `300` | Length of the normalized SQL shown in reports. |

See [the runtime guard](../runtime.md) for how scopes nest, how work is grouped and what a report looks like.
