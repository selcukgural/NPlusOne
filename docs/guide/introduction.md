---
sidebar_position: 1
---

# Introduction

An N+1 problem is code that loads a list with one query and then makes one more round trip to the database for every
element of it: N+1 round trips where one or two would do. Each is fast on its own, so the code passes review and works
on a developer's ten rows; in production, with ten thousand rows, the page takes seconds and the database spends its time
answering the same question with a different id.

```csharp
var customers = await db.Customers.ToListAsync();          // 1 query
foreach (var customer in customers)
{
    var orders = await db.Orders                            // + 1 query per customer
        .Where(o => o.CustomerId == customer.Id)
        .ToListAsync();
}
```

The loop above is the easy case. N+1 hides better than that: in a lazy-loaded navigation read in a `Select`, in a
repository method called from a helper called from a loop, in a MediatR handler sent once per item, in an AutoMapper
profile, in the JSON serializer walking the navigations of entities an endpoint returns, in a `ToString` override, in a
FluentValidation rule.

## Two layers

N+1 finds these problems in two ways, because neither can see everything.

| | Analyzer (`NPlusOne.Analyzers`) | Runtime guard (`NPlusOne.EntityFrameworkCore`, `NPlusOne.AspNetCore`) |
|---|---|---|
| When | Every build, and as you type | Tests and requests, inside a scope |
| Sees | Every code path, even ones no test runs | Every query that really runs, wherever it comes from |
| Misses | Calls whose target is only known at run time (reflection, `dynamic`, an implementation in an assembly the project does not reference) | Code that no test or request runs |
| Reports | Compiler warnings `NP0001`–`NP0007`, with code fixes | A failing assertion, an exception, or a log warning |

Use the analyzer everywhere; add the guard to the integration tests of the code that matters most.

## What it promises

- **Precision first.** A warning (NP0001–NP0005) means the analysis proved that the code makes a round trip and that it
  runs once per element. When it cannot prove it, it stays quiet or, for the two rules meant for educated guesses
  (NP0006, NP0007), suggests. A false warning costs more trust than a missed one; the guard catches what the analyzer
  misses.
- **Proven on a real database.** Every case the analyzer knows, reported or silent, is a test that runs on SQLite and
  counts the SQL: a reported case must send more commands for more rows, a silent one the same number. See the
  [catalogue](../catalogue.md).
- **Free.** MIT licensed, no telemetry, no account.

Next: [get started in five minutes](getting-started.md).
