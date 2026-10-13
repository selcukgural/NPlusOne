---
sidebar_position: 3
---

# Packages

N+1 ships as three NuGet packages, one for each place an N+1 can be caught: the compiler, the test run and the running
web application. They are released together under one version number. All three are listed on
[NuGet](https://www.nuget.org/packages?q=owner%3Aselcukgural+NPlusOne).

| Package | Version | Catches N+1 | Add it to |
|---|---|---|---|
| [NPlusOne.Analyzers](https://www.nuget.org/packages/NPlusOne.Analyzers) | ![NuGet](https://img.shields.io/nuget/v/NPlusOne.Analyzers.svg) | at build time, in the IDE and in CI | every project with EF Core code, and the projects that call it |
| [NPlusOne.EntityFrameworkCore](https://www.nuget.org/packages/NPlusOne.EntityFrameworkCore) | ![NuGet](https://img.shields.io/nuget/v/NPlusOne.EntityFrameworkCore.svg) | in tests, or in any block of code you wrap in a scope | test projects, and the project that configures the `DbContext` |
| [NPlusOne.AspNetCore](https://www.nuget.org/packages/NPlusOne.AspNetCore) | ![NuGet](https://img.shields.io/nuget/v/NPlusOne.AspNetCore.svg) | in HTTP requests, one scope per request | the ASP.NET Core application |

## Which ones you need

- **Any EF Core code:** start with `NPlusOne.Analyzers`. It costs nothing at run time and reports what it can prove.
- **Tests that touch a database:** add `NPlusOne.EntityFrameworkCore` to catch what no compiler can see (queries behind
  interfaces in other assemblies, reflection, configuration decided at run time).
- **A web application:** add `NPlusOne.AspNetCore` to watch every request in development, or to fail chosen endpoints
  in integration tests. It brings `NPlusOne.EntityFrameworkCore` with it.

The analyzer and the guard do not depend on each other: use either one alone, or both.

## NPlusOne.Analyzers

```bash
dotnet add package NPlusOne.Analyzers
```

A Roslyn analyzer with its code fixes, and a source generator that writes a summary of each assembly's data access so
that calls into other projects of the solution can be followed. It is a development dependency: it runs in the
compiler and adds nothing to your application's output. Works with EF Core 8, 9 and 10 and any SDK since .NET 8.

Read on: [the analyzer](analyzer.md), [the rules](../rules/NP0001.md), [configuration](configuration.md).

## NPlusOne.EntityFrameworkCore

```bash
dotnet add package NPlusOne.EntityFrameworkCore
```

The runtime guard: `UseNPlusOneGuard()` on the `DbContext` options, `NPlusOneGuard.Begin()` to open a scope and
`AssertNoNPlusOne()` to fail a test. Reports carry normalized SQL and call sites, never parameter values. Targets
net8.0, net9.0 and net10.0 (EF Core 8, 9 and 10), works with any test framework and needs no dependency injection.

Read on: [the runtime guard](../runtime.md), [in CI](ci.md).

## NPlusOne.AspNetCore

```bash
dotnet add package NPlusOne.AspNetCore
```

`AddNPlusOneGuard()` and `UseNPlusOneGuard()` open one guard scope per request; `[NPlusOneAssert]` (MVC) and
`.WithNPlusOneAssert()` (minimal APIs) make chosen endpoints fail when they repeat a query. Targets net8.0, net9.0 and
net10.0.

Read on: [the runtime guard in ASP.NET Core](../runtime.md#in-aspnet-core).

## Versions and changes

Each release is listed in the [changelog](https://github.com/selcukgural/NPlusOne/blob/main/CHANGELOG.md) and on
[GitHub releases](https://github.com/selcukgural/NPlusOne/releases). Keep the three packages on the same version.
