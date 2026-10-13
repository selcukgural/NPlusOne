# Coding guidelines

These rules apply to all code in this repository. Rules marked **(enforced)** fail the build
through `.editorconfig`, `Directory.Build.props` and the .NET analyzers; the rest are checked in
review.

## Build and dependencies

- **One place for build settings.** Shared properties live in `Directory.Build.props`, package
  versions in `Directory.Packages.props` (central package management). A `.csproj` only declares
  what is specific to that project: target frameworks, output type, project references, a package
  no other project needs. `PackageReference` items never carry a `Version`. **(enforced)**
- **Lock files are committed.** `packages.lock.json` is generated on restore; CI restores in
  locked mode (`CI=true`).
- **No large libraries for small jobs.** The packages we publish end up in other people's builds
  and processes. The analyzer depends on Roslyn only; the runtime packages on EF Core and ASP.NET
  Core only. Twenty lines of our own code beat a dependency tree.

## Language

- **Latest C# syntax.** `LangVersion` is `latest`. Use file-scoped namespaces, primary
  constructors, collection expressions (`[]`, `[.. items]`), pattern matching, switch
  expressions, raw string literals and `required`/`init` where they fit. The analyzer targets
  `netstandard2.0`; the polyfills in `src/NPlusOne.Analyzers/Polyfills` make these features
  compile there. **(enforced where an analyzer exists)**
- **Follow Rider/ReSharper conventions.** `var` everywhere, `_camelCase` private fields,
  `PascalCase` constants and static readonly fields, `I` prefix for interfaces, explicit
  accessibility modifiers, one top-level type per file (small related records may share a file).
  **(enforced where an analyzer exists)**
- **Braces always**, even around a one-line `if`, `else`, `for`, `foreach` or `while` body.
  **(enforced, IDE0011)**

## Control flow

- **Guard clauses and early returns.** Validate and exit at the top of a method; keep the main
  path unindented. Prefer `continue` over wrapping a loop body in an `if`.
- **At most five parameters.** A constructor or method that needs more takes a parameter object,
  usually a `record` named after what it describes. Optional flags go into that object too, not
  into boolean parameters.

## Comments

- **Every type and member has an XML comment**: classes, records, interfaces, enums and their
  values, methods, properties, constructors, including private and internal ones. Document every
  parameter with `<param>`. The compiler enforces it for public members; review checks the rest.
  Test projects are exempt; test names describe the behaviour. **(enforced for public members)**
- **Write for the next developer.** Explain *what* the member does and *why* it is done this way:
  intent, invariants, edge cases, performance reasons. Do not restate the signature, narrate the
  code line by line, or leave notes addressed to an AI or about how the code was written.

## The analyzer

- **A rule is a promise about precision.** A warning must point at code that really runs a query
  (or a lazy load, or a save) once per element. When the analysis cannot prove it, it stays quiet
  in the warning rules and may say so in the `Info` rule meant for guesses (NP0006). A false
  warning costs more trust than a missed one: the runtime guard catches what the analyzer misses.
- **Every rule change comes with corpus cases.** `corpus/` holds C# files whose lines are marked
  with the diagnostics they must produce (`// expect: NP0001`); an unmarked line must produce none.
  A fix for a missed case or a false positive adds that case to the corpus first.
- **Every fix is verified both ways.** The cases cover both sides: what must stay silent and what
  must still be reported, so a fix cannot hide real N+1s. They fail before the fix and pass after it
  (red, then green). Then `tools/countercheck.py` undoes each part of the fix in turn: every part
  must turn its cases red again, or no test guards it.
- **Every marker is proven at run time.** `GroundTruthTests` runs the corpus on SQLite and counts the SQL each
  method sends for 3 and 6 seeded rows per entity: a reported case must grow, a silent one must not. What EF
  Core really does decides, not what we believe it does; behaviour that differs between EF Core versions is
  read from the referenced version (`KnownSymbols.EfCoreMajorVersion`).
- **Messages tell the user what to do.** A diagnostic names the query, the loop and the call
  chain that connects them, and suggests the concrete rewrite (an `Include`, a preload with
  `Contains` and `ToLookup`, a projection).
- **Analyzers run on every keystroke.** Cache per compilation, never per call; bound every
  walk (call depth, number of implementations); no LINQ in per-operation callbacks when a plain
  loop is as clear. Banned analyzer APIs (file and network access, `Console`) stay banned (RS1035).
- **A keystroke must not cost a whole compilation.** An IDE makes a new compilation on every edit. A
  scan over every file reads the per-tree `SyntaxIndex` (kept with the tree, so only the edited file
  is walked again), never the files' text; ask the syntax before the symbols when the syntax can rule
  a type out. What depends only on declarations, or on what an unchanged file binds to, is kept
  across compilations in `DeclarationFacts`, by name and never as symbols, and an `IncrementalTests`
  case shows an edit that changes it is not answered from the previous compilation. Measure with
  `PerformanceTests` before and after.
- **Every rule has a page** in `docs/rules/` with what it detects, why it matters, how to fix it,
  how to configure it and what it cannot see.

## The runtime guard

- **Never on by surprise.** The guard is opt-in per scope (a request, a test, a manual scope) and
  costs nothing when no scope is active.
- **No SQL values in reports.** Reports carry the normalized fingerprint (literals and parameters
  replaced), never parameter values, so a log or an exception message cannot leak data.

## Performance

- **Avoid needless allocations** on paths that run per operation or per command: no string
  concatenation for comparisons, no LINQ in hot loops when a plain loop is as clear, no
  `ToList()`/`ToArray()` just to iterate once.
- Use `static` lambdas when nothing is captured; `FrozenDictionary`/`FrozenSet` for static lookup
  tables where the target framework has them, `ImmutableDictionary`/`ImmutableHashSet` in the
  analyzer.
- Measure before optimizing anything more than this; readability wins without numbers.
