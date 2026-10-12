---
sidebar_position: 20
---

# FAQ and troubleshooting

## A warning I expected does not appear

- **Is the code reachable through a call the analyzer can follow?** Calls through reflection, `dynamic`, or an
  implementation in an assembly the project does not reference are not followed. Add the package to the project that
  holds the implementation (its summaries then travel with its assembly), declare the method in
  `nplusone.query_methods`, or cover the path with the [runtime guard](../runtime.md).
- **Does it depend on another project?** Cross-project warnings appear when you build, not while you type: the IDE does
  not run the source generator that writes the summaries.
- **Lazy-loading rules (NP0002, NP0005, NP0007)** need lazy loading on. In a project that does not reference
  `Microsoft.EntityFrameworkCore.Proxies` itself, set `nplusone.lazy_loading = enabled`.
- **Is it deeper than four calls?** Raise `nplusone.max_call_depth`.

## A warning looks wrong

First check whether it really is: the [catalogue](../catalogue.md) lists the shapes that look like N+1 but are not
(`Include`d navigations, batches, paging, polling, cached lookups, expression trees) and the analyzer stays quiet on all
of them. If you still think the warning is wrong, please open an issue with a small snippet: every false warning becomes a
test case and a fix. Until then, suppress it with `#pragma warning disable` and a comment.

## Does it support Dapper, raw ADO.NET or GraphQL resolvers?

Not yet: both layers understand EF Core. GraphQL resolvers that load per parent without a DataLoader are next on the
list; Dapper and ADO.NET come after that.

## Which versions are supported?

EF Core 8, 9 and 10. The analyzer loads in every compiler since the .NET 8 SDK (Roslyn 4.8); the runtime packages
target `net8.0`, `net9.0` and `net10.0`.

## Does the guard slow down production?

With no scope active, each command costs one async-local read. Inside a scope it records a fingerprint (and, by default,
a stack frame) per command. Enable the middleware where you want it: development, staging, or a share of production
traffic.

## Can a report leak data?

No. Reports carry the SQL with every literal and parameter replaced, never parameter values, so they are safe to log
and to put in exception messages.

## How does it compare with other tools?

See [the comparison](../comparison.md): other .NET tools measured on the same cases.
