; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
NP0001 | Performance | Warning | A query runs once per element (N+1 read)
NP0002 | Performance | Warning | A lazy-loaded navigation is read once per element
NP0003 | Performance | Warning | SaveChanges runs once per element (N+1 write)
NP0004 | Performance | Warning | A recursive method runs a query on every call
NP0005 | Performance | Warning | An endpoint returns entities whose lazy navigations the serializer loads one by one
NP0006 | Performance | Info | A data-access abstraction is called once per element and its implementation is not visible
NP0007 | Performance | Info | A mapper reads lazy navigations of each element by naming convention
