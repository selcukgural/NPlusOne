---
sidebar_position: 5
---

# In CI

N+1 belongs in the pull request, not in the incident review. Both layers run in a normal CI build.

## Gate on the analyzer

Raise the rules you want enforced to errors in `.editorconfig` (see [Getting started](getting-started.md#4-fail-the-build-on-n1)):
the build fails on a new N+1 like on any compiler error. Nothing else is needed: the analyzer runs in `dotnet build`.

Adopting it on an existing code base with many warnings? Raise the rules to errors and suppress the existing findings in
a `GlobalSuppressions.cs` file (the IDE's "Suppress in suppression file" action writes it), then pay them down: new code
is held to the rule from day one.

A `.globalconfig` with `nplusone.query_methods` for the clients your code calls (another service, a package without
summaries) lets the analyzer report those too.

## Gate on the guard

Integration tests that run your services against a real database (SQLite, a container, Testcontainers) are where the
guard pays off. Attach it once, in the fixture that builds the context, and assert in each test:

```csharp
public sealed class DatabaseFixture
{
    public ShopContext CreateContext()
        => new(new DbContextOptionsBuilder<ShopContext>()
            .UseNpgsql(ConnectionString)
            .UseNPlusOneGuard()
            .Options);
}

[Fact]
public async Task Checkout_does_not_query_per_line()
{
    await using var db = _fixture.CreateContext();
    using var guard = NPlusOneGuard.Begin(o => o.IncludeWrites = true);

    await new CheckoutService(db).CheckoutAsync(cartId);

    guard.AssertNoNPlusOne();
}
```

For tests that go through HTTP (`WebApplicationFactory`), add `[NPlusOneAssert]` to the controllers or
`.WithNPlusOneAssert()` to the endpoint groups under test: the request fails with the report when an N+1 happens. The
filters fail requests in every environment but Production, where they only log a warning, so leaving them on an
endpoint cannot let the data take it down.

## GitHub Actions

Nothing specific: `dotnet build` and `dotnet test` run both layers. The analyzer's warnings appear as annotations on the
changed lines when you build with the default logger.

```yaml
- run: dotnet build --configuration Release
- run: dotnet test --configuration Release --no-build
```
