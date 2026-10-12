using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Model;

/// <summary>
/// The framework types the analysis recognises, resolved once per compilation. EF Core types are
/// required (without them nothing can run a query); the others are optional and simply never match
/// when the compilation does not reference them.
/// </summary>
internal sealed class KnownSymbols
{
    /// <summary>Resolves the symbols from a compilation that references EF Core.</summary>
    /// <param name="compilation">The compilation being analysed.</param>
    /// <param name="dbContext">EF Core's <c>DbContext</c>, already resolved by <see cref="TryCreate"/>.</param>
    private KnownSymbols(Compilation compilation, INamedTypeSymbol dbContext)
    {
        DbContext = dbContext;
        EfCoreMajorVersion = dbContext.ContainingAssembly.Identity.Version.Major;
        DbSet = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbSet`1");
        Queryable = compilation.GetTypeByMetadataName("System.Linq.Queryable");
        Enumerable = compilation.GetTypeByMetadataName("System.Linq.Enumerable");
        AsyncEnumerable = compilation.GetTypeByMetadataName("System.Linq.AsyncEnumerable");
        IQueryable = compilation.GetTypeByMetadataName("System.Linq.IQueryable");
        IEnumerableOfT = compilation.GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T);
        IAsyncEnumerableOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1");
        Expression = compilation.GetTypeByMetadataName("System.Linq.Expressions.Expression`1");
        EfQueryableExtensions = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions");
        RelationalQueryableExtensions = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.RelationalQueryableExtensions");
        RelationalDatabaseFacadeExtensions = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions");
        DatabaseFacade = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade");
        EntityEntry = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry");
        NavigationEntry = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.ChangeTracking.NavigationEntry");
        CollectionEntry = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.ChangeTracking.CollectionEntry");
        EntityTypeConfiguration = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1");
        LazyLoader = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.Infrastructure.ILazyLoader");
        ProxiesExtensions = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.ProxiesExtensions");
        ListOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        Array = compilation.GetSpecialType(SpecialType.System_Array);
        Parallel = compilation.GetTypeByMetadataName("System.Threading.Tasks.Parallel");
        Task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        TaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        ValueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        Thread = compilation.GetTypeByMetadataName("System.Threading.Thread");
        ControllerBase = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ControllerBase");
        ApiControllerAttribute = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ApiControllerAttribute");
        NonActionAttribute = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.NonActionAttribute");
        ActionResultOfT = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ActionResult`1");
        Results = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.Results");
        TypedResults = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.TypedResults");
        ObjectResult = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ObjectResult");
        JsonResult = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.JsonResult");
        Hub = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.SignalR.Hub");
    }

    /// <summary><c>Microsoft.EntityFrameworkCore.DbContext</c>.</summary>
    public INamedTypeSymbol DbContext { get; }

    /// <summary>
    /// The major version of the EF Core the compilation references (8 for EF Core 8). Behaviour that changed
    /// between versions depends on it: from EF Core 8 on, entities of no-tracking queries load lazily too.
    /// </summary>
    public int EfCoreMajorVersion { get; }

    /// <summary><c>DbSet&lt;T&gt;</c>.</summary>
    public INamedTypeSymbol? DbSet { get; }

    /// <summary><c>System.Linq.Queryable</c>: the operators whose lambdas are expression trees.</summary>
    public INamedTypeSymbol? Queryable { get; }

    /// <summary><c>System.Linq.Enumerable</c>: the operators whose lambdas run once per element.</summary>
    public INamedTypeSymbol? Enumerable { get; }

    /// <summary><c>System.Linq.AsyncEnumerable</c> (built in since .NET 10, or System.Linq.Async).</summary>
    public INamedTypeSymbol? AsyncEnumerable { get; }

    /// <summary>The non-generic <c>IQueryable</c>, which every query type implements.</summary>
    public INamedTypeSymbol? IQueryable { get; }

    /// <summary><c>IEnumerable&lt;T&gt;</c>.</summary>
    public INamedTypeSymbol IEnumerableOfT { get; }

    /// <summary><c>IAsyncEnumerable&lt;T&gt;</c>.</summary>
    public INamedTypeSymbol? IAsyncEnumerableOfT { get; }

    /// <summary><c>Expression&lt;TDelegate&gt;</c>: a lambda converted to it is translated, not run.</summary>
    public INamedTypeSymbol? Expression { get; }

    /// <summary>EF Core's query operators (<c>ToListAsync</c>, <c>Include</c>, ...).</summary>
    public INamedTypeSymbol? EfQueryableExtensions { get; }

    /// <summary>EF Core's relational query operators (<c>FromSql</c>, <c>AsSplitQuery</c>, <c>ExecuteUpdate</c> before EF 9).</summary>
    public INamedTypeSymbol? RelationalQueryableExtensions { get; }

    /// <summary>EF Core's raw SQL methods on <c>Database</c> (<c>ExecuteSql</c>, <c>SqlQuery</c>).</summary>
    public INamedTypeSymbol? RelationalDatabaseFacadeExtensions { get; }

    /// <summary><c>DbContext.Database</c>'s type.</summary>
    public INamedTypeSymbol? DatabaseFacade { get; }

    /// <summary><c>EntityEntry</c>: <c>Reload</c> and <c>GetDatabaseValues</c> run a query.</summary>
    public INamedTypeSymbol? EntityEntry { get; }

    /// <summary><c>NavigationEntry</c>, the base of <c>ReferenceEntry</c> and <c>CollectionEntry</c>: <c>Load</c> runs a query.</summary>
    public INamedTypeSymbol? NavigationEntry { get; }

    /// <summary><c>CollectionEntry</c>: its <c>Query()</c> starts an EF-backed query.</summary>
    public INamedTypeSymbol? CollectionEntry { get; }

    /// <summary><c>IEntityTypeConfiguration&lt;T&gt;</c>: its type argument is an entity.</summary>
    public INamedTypeSymbol? EntityTypeConfiguration { get; }

    /// <summary><c>ILazyLoader</c>: an entity that takes it loads its navigations lazily without proxies.</summary>
    public INamedTypeSymbol? LazyLoader { get; }

    /// <summary><c>ProxiesExtensions</c> (<c>UseLazyLoadingProxies</c>): present when the proxies package is referenced.</summary>
    public INamedTypeSymbol? ProxiesExtensions { get; }

    /// <summary><c>List&lt;T&gt;</c> (<c>ForEach</c>).</summary>
    public INamedTypeSymbol? ListOfT { get; }

    /// <summary><c>System.Array</c> (<c>ForEach</c>).</summary>
    public INamedTypeSymbol Array { get; }

    /// <summary><c>System.Threading.Tasks.Parallel</c>.</summary>
    public INamedTypeSymbol? Parallel { get; }

    /// <summary><c>Task</c> (<c>Delay</c> marks a polling loop).</summary>
    public INamedTypeSymbol? Task { get; }

    /// <summary><c>Task&lt;T&gt;</c>.</summary>
    public INamedTypeSymbol? TaskOfT { get; }

    /// <summary><c>ValueTask&lt;T&gt;</c>.</summary>
    public INamedTypeSymbol? ValueTaskOfT { get; }

    /// <summary><c>Thread</c> (<c>Sleep</c> marks a polling loop).</summary>
    public INamedTypeSymbol? Thread { get; }

    /// <summary>ASP.NET Core MVC's <c>ControllerBase</c>.</summary>
    public INamedTypeSymbol? ControllerBase { get; }

    /// <summary><c>[ApiController]</c>.</summary>
    public INamedTypeSymbol? ApiControllerAttribute { get; }

    /// <summary><c>[NonAction]</c>.</summary>
    public INamedTypeSymbol? NonActionAttribute { get; }

    /// <summary><c>ActionResult&lt;T&gt;</c>.</summary>
    public INamedTypeSymbol? ActionResultOfT { get; }

    /// <summary>Minimal APIs' <c>Results</c>.</summary>
    public INamedTypeSymbol? Results { get; }

    /// <summary>Minimal APIs' <c>TypedResults</c>.</summary>
    public INamedTypeSymbol? TypedResults { get; }

    /// <summary>MVC's <c>ObjectResult</c>, the base of <c>OkObjectResult</c> and the other results that serialize a value.</summary>
    public INamedTypeSymbol? ObjectResult { get; }

    /// <summary>MVC's <c>JsonResult</c>.</summary>
    public INamedTypeSymbol? JsonResult { get; }

    /// <summary>SignalR's <c>Hub</c>, the base of <c>Hub&lt;T&gt;</c>: its public methods return values to the caller.</summary>
    public INamedTypeSymbol? Hub { get; }

    /// <summary>Resolves the symbols, or returns null when the compilation does not reference EF Core.</summary>
    /// <param name="compilation">The compilation being analysed.</param>
    public static KnownSymbols? TryCreate(Compilation compilation)
    {
        var dbContext = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbContext");
        return dbContext is null ? null : new KnownSymbols(compilation, dbContext);
    }

    /// <summary>Whether <paramref name="type"/> is or derives from <paramref name="baseType"/>.</summary>
    /// <param name="type">The type to test.</param>
    /// <param name="baseType">The base class.</param>
    public static bool InheritsFrom(ITypeSymbol? type, INamedTypeSymbol? baseType)
    {
        if (baseType is null)
        {
            return false;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="type"/> is a query: it implements <c>IQueryable</c>.</summary>
    /// <param name="type">The static type of an expression.</param>
    public bool IsQueryable(ITypeSymbol? type)
    {
        if (type is null || IQueryable is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(type, IQueryable))
        {
            return true;
        }

        foreach (var i in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(i, IQueryable))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The element type of a sequence (<c>IEnumerable&lt;T&gt;</c> or <c>IAsyncEnumerable&lt;T&gt;</c>), or
    /// null when <paramref name="type"/> is not one. Strings are not sequences here.
    /// </summary>
    /// <param name="type">The type to inspect.</param>
    public ITypeSymbol? ElementType(ITypeSymbol? type)
    {
        if (type is null || type.SpecialType == SpecialType.System_String)
        {
            return null;
        }

        if (type is IArrayTypeSymbol array)
        {
            return array.ElementType;
        }

        if (type is INamedTypeSymbol named && IsSequenceDefinition(named.OriginalDefinition))
        {
            return named.TypeArguments[0];
        }

        foreach (var i in type.AllInterfaces)
        {
            if (IsSequenceDefinition(i.OriginalDefinition))
            {
                return i.TypeArguments[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Unwraps the types a method returns its value in: <c>Task&lt;T&gt;</c>, <c>ValueTask&lt;T&gt;</c> and
    /// <c>ActionResult&lt;T&gt;</c>, repeatedly.
    /// </summary>
    /// <param name="type">The declared type.</param>
    public ITypeSymbol? Unwrap(ITypeSymbol? type)
    {
        while (type is INamedTypeSymbol { IsGenericType: true } named
               && (SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, TaskOfT)
                   || SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, ValueTaskOfT)
                   || SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, ActionResultOfT)))
        {
            type = named.TypeArguments[0];
        }

        return type;
    }

    /// <summary>Whether a generic definition is <c>IEnumerable&lt;T&gt;</c> or <c>IAsyncEnumerable&lt;T&gt;</c>.</summary>
    /// <param name="definition">The original definition of a named type.</param>
    private bool IsSequenceDefinition(INamedTypeSymbol definition)
        => SymbolEqualityComparer.Default.Equals(definition, IEnumerableOfT)
           || SymbolEqualityComparer.Default.Equals(definition, IAsyncEnumerableOfT);
}
