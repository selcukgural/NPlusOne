using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Model;

/// <summary>
/// The framework types the analysis recognises, resolved once per compilation, when the compilation has data access the
/// analysis knows of (<see cref="TryCreate"/>); a type the compilation does not reference is null and simply never matches.
/// </summary>
internal sealed class KnownSymbols
{
    /// <summary>Resolves the symbols from a compilation with data access.</summary>
    /// <param name="compilation">The compilation being analysed.</param>
    /// <param name="dbContext">EF Core's <c>DbContext</c>, already resolved by <see cref="TryCreate"/>; null in a layer without EF Core.</param>
    /// <param name="abpRepository">ABP's <c>IReadOnlyBasicRepository&lt;TEntity&gt;</c>, already resolved by <see cref="TryCreate"/>.</param>
    private KnownSymbols(Compilation compilation, INamedTypeSymbol? dbContext, INamedTypeSymbol? abpRepository)
    {
        DbContext = dbContext;
        EfCoreMajorVersion = dbContext?.ContainingAssembly.Identity.Version.Major ?? 0;
        AbpRepository = abpRepository;
        AbpRepositoryAsyncExtensions = compilation.GetTypeByMetadataName("Volo.Abp.Domain.Repositories.RepositoryAsyncExtensions");
        AbpRepositoryExtensions = compilation.GetTypeByMetadataName("Volo.Abp.Domain.Repositories.RepositoryExtensions");
        AbpAsyncQueryableExecuter = compilation.GetTypeByMetadataName("Volo.Abp.Linq.IAsyncQueryableExecuter");
        AbpEntity = compilation.GetTypeByMetadataName("Volo.Abp.Domain.Entities.IEntity");
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
        ModelBuilder = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.ModelBuilder");
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

    /// <summary><c>Microsoft.EntityFrameworkCore.DbContext</c>; null in a project that does not reference EF Core (an application layer).</summary>
    public INamedTypeSymbol? DbContext { get; }

    /// <summary>
    /// The major version of the EF Core the compilation references (8 for EF Core 8), 0 when it references none. Behaviour
    /// that changed between versions depends on it: from EF Core 8 on, entities of no-tracking queries load lazily too.
    /// </summary>
    public int EfCoreMajorVersion { get; }

    /// <summary>
    /// ABP's <c>IReadOnlyBasicRepository&lt;TEntity&gt;</c>, the base of every ABP repository: a repository is an entity set
    /// whose methods run queries, and an ABP application layer sees no EF Core, only these.
    /// </summary>
    public INamedTypeSymbol? AbpRepository { get; }

    /// <summary>ABP's <c>RepositoryAsyncExtensions</c>: <c>repository.FirstOrDefaultAsync(...)</c>, <c>CountAsync</c> and the other operators run the repository's query.</summary>
    public INamedTypeSymbol? AbpRepositoryAsyncExtensions { get; }

    /// <summary>ABP's <c>RepositoryExtensions</c>: explicit loading, <c>EnsureExistsAsync</c> and <c>HardDeleteAsync</c>.</summary>
    public INamedTypeSymbol? AbpRepositoryExtensions { get; }

    /// <summary>ABP's <c>IAsyncQueryableExecuter</c> (<c>AsyncExecuter</c>): runs the query it is given, without referencing EF Core.</summary>
    public INamedTypeSymbol? AbpAsyncQueryableExecuter { get; }

    /// <summary>ABP's <c>IEntity</c>: the types implementing it are entities where no <c>DbContext</c> is visible.</summary>
    public INamedTypeSymbol? AbpEntity { get; }

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

    /// <summary><c>ModelBuilder</c>: the type its <c>Entity&lt;T&gt;()</c> and <c>Entity(typeof(T))</c> map is an entity.</summary>
    public INamedTypeSymbol? ModelBuilder { get; }

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

    /// <summary>
    /// Resolves the symbols, or returns null when nothing in the compilation can make a round trip the analysis knows of:
    /// it references neither EF Core nor ABP's repositories, declares no <c>nplusone.query_methods</c>, and neither it nor a
    /// referenced project has a type named like data access (<c>nplusone.data_access_types</c>). An application layer
    /// that calls <c>IOrderRepository</c> without referencing EF Core is analysed (NP0006); a project with no data access
    /// costs nothing.
    /// </summary>
    /// <param name="compilation">The compilation being analysed.</param>
    /// <param name="settings">The analyzer's options for it.</param>
    public static KnownSymbols? TryCreate(Compilation compilation, Settings settings)
    {
        var dbContext = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbContext");
        var abpRepository = compilation.GetTypeByMetadataName("Volo.Abp.Domain.Repositories.IReadOnlyBasicRepository`1");
        return dbContext is not null || abpRepository is not null || !settings.QueryMethods.IsEmpty || NamesDataAccess(compilation, settings)
            ? new KnownSymbols(compilation, dbContext, abpRepository)
            : null;
    }

    /// <summary>Whether the compilation or a referenced non-framework assembly declares a type named like data access.</summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="settings">Its options, with the data access type patterns.</param>
    private static bool NamesDataAccess(Compilation compilation, Settings settings)
    {
        if (settings.DataAccessTypes.MayMatchAnyType(compilation.Assembly.TypeNames))
        {
            return true;
        }

        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (!EntityModel.IsFrameworkAssembly(assembly.Name) && settings.DataAccessTypes.MayMatchAnyType(assembly.TypeNames))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a type is an ABP repository: it is or implements <c>IReadOnlyBasicRepository&lt;TEntity&gt;</c>.</summary>
    /// <param name="type">The static type of an expression.</param>
    public bool IsAbpRepository(ITypeSymbol? type)
    {
        if (type is null || AbpRepository is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, AbpRepository))
        {
            return true;
        }

        foreach (var i in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, AbpRepository))
            {
                return true;
            }
        }

        return false;
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
