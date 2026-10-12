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
- `src/NPlusOne.EntityFrameworkCore` and `src/NPlusOne.AspNetCore` (net8.0 + net9.0 + net10.0, EF Core 8, 9
  and 10): the runtime guard. Tests run on all three; the .NET 8, 9 and 10 runtimes must be installed.
- `docs/backlog.md` is the work list, taken in order. Each item starts with failing corpus cases
  (both what must stay silent and what must still be reported); after the fix,
  `python3 tools/countercheck.py tools/countercheck/<item>.json` undoes each part and must turn every
  part red. Then strike the heading through with the date and a line on what was done, in the same change.
- `tools/scan.sh /path/to/repo [sln]` runs the analyzer over another solution without changing it (copies it,
  injects the analyzer); use it to triage real projects. Scanned so far: four private applications (12 real N+1),
  eShop (8), ardalis/CleanArchitecture (1), jasontaylordev/CleanArchitecture (0), all real after the fixes; findings
  are recorded in docs/backlog.md and docs/comparison.md.
- `ComparisonTests` (`NPLUSONE_COMPARE=1`) scores other tools on the corpus next to this one: LinqContraband (static),
  and EFCore.QueryGuard, DbEye and MiniProfiler (runtime, through `IRuntimeDetector` in the ground-truth harness).

Follow the coding guidelines below for every change, and keep `CONTRIBUTING.md` up to date when a
rule changes.

@CONTRIBUTING.md
