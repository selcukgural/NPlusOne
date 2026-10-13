# Corpus

Every folder except `Shared` is compiled as one project, together with the files in `Shared`, and run through the
analyzer. A line that must produce diagnostics ends with `// expect: NP0001` (several ids: `// expect: NP0001, NP0002`);
every other line must produce none. A folder's `.globalconfig` sets its analyzer options, as it would in a real project.
The harness also reads `harness.exclude_references` from it, a `;`-separated list of assembly names left out of the
compilation (to see how the analyzer behaves without EF Core proxies, for example; a name ending in `*` is a prefix,
`Volo.Abp.*`), and `harness.shared = false`, which leaves the files in `Shared` out (an application layer that references
no EF Core cannot compile the shared model).

`Abp` and `AbpEfCore` compile against ABP Framework's real packages (8.3, 9.3 and 10.7, one per EF Core major). A folder
that declares an ABP module runs under ABP in `GroundTruthTests`: an application with Autofac whose repositories are
ABP's own `EfCoreRepository`, every method in one unit of work; a folder without an ABP context (`Abp`, an application
layer compiled without EF Core) has its entities mapped by the harness' `AbpHarnessDbContext`.

The test project references the real MediatR, Mediator, AutoMapper, Mapster, Newtonsoft.Json and ABP packages, so the
corpus uses their actual APIs; a folder can leave them out like any other reference. The running framework's
`NETn_0_OR_GREATER` symbols are defined: a case that needs a newer API goes inside `#if NET10_0_OR_GREATER`, and its
markers count only where it compiles.

`GroundTruthTests` also runs every method of the corpus against SQLite and checks the markers against the SQL it
really sends (see `tests/NPlusOne.Analyzers.Tests/GroundTruthTests.cs`): a marked method must send more commands
for more seeded rows, an unmarked one the same number. `// ground-truth: reason` on a line of a method explains a
marked method that cannot run, or one the row count does not drive; `// ground-truth-arguments: new` gives a
method new entities instead of loaded ones; `// ground-truth-proves: NP0001 (where)` makes an unmarked method that runs
a marked line the harness cannot run itself (a rule in a validator's constructor) prove it. A public static corpus method taking MediatR's `MediatRServiceConfiguration` is called
when MediatR is registered, so the corpus registers its pipeline behaviors as an application would.

The corpus is the measure of the analyzer: recall is the share of `expect` markers reported, precision the share of
reported diagnostics that have a marker. Add the case here before fixing a missed diagnostic or a false one.
