# N+1 (NPlusOne)

Finds N+1 query problems in .NET / EF Core code: a Roslyn analyzer at build time and a runtime
guard in tests and requests. MIT licensed, free for the community.

- Build: `dotnet build` (warnings are errors) · Test: `dotnet test`
- The SDK is pinned to 10.0 in `global.json`.
- `src/NPlusOne.Analyzers` (netstandard2.0, Roslyn 4.8): the rules NP0001–NP0007, see docs/design.md
  and docs/rules/. `src/NPlusOne.Analyzers.CodeFixes`: the Include and SaveChanges fixes.
  `src/NPlusOne.Analyzers.Package` packs both as the `NPlusOne.Analyzers` NuGet package.
- `corpus/` holds the recall/precision corpus (`// expect: NPxxxx` markers) that
  `tests/NPlusOne.Analyzers.Tests` compiles and checks on EF Core 8, 9 and 10; every rule change adds
  corpus cases. `NPLUSONE_DUMP=1 dotnet test --filter DumpTests --logger "console;verbosity=detailed"`
  prints every corpus diagnostic with its message.
- `RuntimeGuardTests` runs the corpus again under the runtime guard: it must flag every method the ground truth proves
  grows and nothing else.
- `GroundTruthTests` runs every corpus method against SQLite (lazy-loading proxies, 3 then 6 seeded rows per
  entity, harness in `tests/NPlusOne.Analyzers.Tests/Harness/GroundTruth`): a marked method must send more SQL
  commands for more rows, an unmarked one the same number; a marked method that cannot run, or one the row
  count does not drive, says why in a `// ground-truth: reason` comment. `NPLUSONE_SQL=1` prints the SQL. A new
  corpus case is proven here too, on every supported EF Core version.
- `corpus/Abp` (an application layer compiled without EF Core) and `corpus/AbpEfCore` compile against ABP Framework
  (8.3, 9.3, 10.7, one per target framework) and run under ABP in the ground truth (`Harness/GroundTruth/AbpHarness.cs`:
  Autofac, ABP's `EfCoreRepository`, a unit of work per method). `corpus/Layered` is a Clean Architecture application
  layer without EF Core (NP0006). Folder options `harness.shared = false` and prefix exclusions (`Volo.Abp.*`) make them.
- `src/NPlusOne.EntityFrameworkCore` and `src/NPlusOne.AspNetCore` (net8.0 + net9.0 + net10.0, EF Core 8, 9
  and 10): the runtime guard. Tests run on all three; the .NET 8, 9 and 10 runtimes must be installed.
- `docs/backlog.md` is the work list, taken in order. Each item starts with failing corpus cases
  (both what must stay silent and what must still be reported); after the fix,
  `python3 tools/countercheck.py tools/countercheck/<item>.json` undoes each part and must turn every
  part red. Then strike the heading through with the date and a line on what was done, in the same change.
- `tools/scan.sh /path/to/repo [sln]` runs the analyzer over another solution without changing it (copies it,
  injects the analyzer); use it to triage real projects. Scanned so far: four private applications (12 real N+1),
  eShop (8), ardalis/CleanArchitecture (1), jasontaylordev/CleanArchitecture (0), abp-samples (1), all real after the fixes; findings
  are recorded in docs/backlog.md and docs/comparison.md.
- `ComparisonTests` (`NPLUSONE_COMPARE=1`) scores other tools on the corpus next to this one: LinqContraband (static),
  and EFCore.QueryGuard, DbEye and MiniProfiler (runtime, through `IRuntimeDetector` in the ground-truth harness).
- `benchmarks/NPlusOne.Benchmarks` (BenchmarkDotNet, net10.0, in process, time and allocations): the runtime guard
  (`FingerprintBenchmarks`, `CallSiteBenchmarks`, `ScopeBenchmarks`, `GuardedRequestBenchmarks` on SQLite) and the analyzer
  (`KeystrokeBenchmarks` for a body and a declaration edit, `BuildBenchmarks`) on the generated application of the analyzer
  tests. `dotnet run -c Release --project benchmarks/NPlusOne.Benchmarks -- --filter '*Fingerprint*'`; run the affected
  benchmarks before and after a performance change and put both numbers in the backlog entry.
- `PerformanceTests` (`NPLUSONE_BENCH=<modules>`, Release, one framework) times one document's analysis after a keystroke
  in a generated application, next to an analyzer that only subscribes to the same operations. `IncrementalTests` edit
  one file at a time, as an IDE does, so what `DeclarationFacts` keeps across compilations never answers for a changed one.

Follow the coding guidelines below for every change, and keep `CONTRIBUTING.md` up to date when a
rule changes.

@CONTRIBUTING.md
