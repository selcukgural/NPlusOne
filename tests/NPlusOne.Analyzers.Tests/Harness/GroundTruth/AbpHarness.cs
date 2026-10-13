using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Application;
using Volo.Abp.Autofac;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.Modeling;
using Volo.Abp.Modularity;
using Volo.Abp.Modularity.PlugIns;
using Volo.Abp.Uow;

namespace NPlusOne.Analyzers.Tests.Harness.GroundTruth;

/// <summary>
/// Runs ABP corpus folders the way an ABP application runs: the corpus' modules are plugged into an application with
/// Autofac (ABP injects properties such as <c>AsyncExecuter</c>), EF Core and the DDD application module; repositories
/// are ABP's own <c>EfCoreRepository</c>, and every run is one unit of work, as an application service call is.
/// </summary>
public static class AbpHarness
{
    /// <summary>The corpus types that are ABP modules: a folder with one runs under ABP.</summary>
    public static Type[] Modules(IEnumerable<Assembly> assemblies)
        => [.. assemblies.SelectMany(a => a.GetTypes()).Where(t => t is { IsClass: true, IsAbstract: false } && typeof(AbpModule).IsAssignableFrom(t))];

    /// <summary>Whether a context is an ABP one, which ABP creates and configures.</summary>
    public static bool IsAbpContext(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AbpDbContext<>))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a corpus class derives from one of ABP's (an application service, a repository): ABP creates it, with the
    /// properties it injects (<c>AsyncExecuter</c>, <c>LazyServiceProvider</c>), so it comes from the container.
    /// </summary>
    public static bool IsAbpService(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Assembly.GetName().Name?.StartsWith("Volo.Abp.", StringComparison.Ordinal) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The corpus' ABP entities when the corpus has no ABP context of its own (an application layer that sees no EF
    /// Core): <see cref="AbpHarnessDbContext"/> maps them, as the application's EF Core layer would.
    /// </summary>
    public static Type[] UnmappedEntities(IEnumerable<Assembly> assemblies)
    {
        var types = assemblies.SelectMany(a => a.GetTypes()).ToList();
        return types.Any(IsAbpContext)
            ? []
            : [.. types.Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && typeof(IEntity).IsAssignableFrom(t))];
    }

    /// <summary>Creates and starts the application on the given services; its provider is Autofac's.</summary>
    public static async Task<(IAbpApplicationWithExternalServiceProvider Application, IServiceProvider Provider)> StartAsync(
        IServiceCollection services, Type[] modules, AbpHarnessSettings settings)
    {
        services.AddSingleton(settings);
        var application = await AbpApplicationFactory.CreateAsync<AbpHarnessModule>(services, options =>
        {
            options.UseAutofac();
            options.PlugInSources.AddTypes(modules);
        });
        var provider = services.BuildServiceProviderFromFactory();
        await application.InitializeAsync(provider);
        return (application, provider);
    }

    /// <summary>A unit of work without a transaction, so the schema can be created and the SQL is the application's alone.</summary>
    public static IUnitOfWork Begin(IServiceProvider services)
        => services.GetRequiredService<IUnitOfWorkManager>().Begin(new AbpUnitOfWorkOptions(isTransactional: false), requiresNew: true);

    /// <summary>The unit of work's context of an ABP context type, as a repository gets it.</summary>
    public static async Task<DbContext> ContextAsync(IServiceProvider services, Type contextType)
    {
        var providerType = typeof(IDbContextProvider<>).MakeGenericType(contextType);
        var provider = services.GetRequiredService(providerType);
        var task = (Task)providerType.GetMethod(nameof(IDbContextProvider<AbpHarnessDbContext>.GetDbContextAsync))!.Invoke(provider, null)!;
        await task;
        return (DbContext)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
}

/// <summary>What the harness module configures: the database every ABP context uses, and the entities it maps for a corpus without a context.</summary>
/// <param name="Configure">Sets up a context's options: SQLite, the command log, lazy loading, a detector.</param>
/// <param name="UnmappedEntities">The entities <see cref="AbpHarnessDbContext"/> maps; empty when the corpus has its own context.</param>
public sealed record AbpHarnessSettings(Action<DbContextOptionsBuilder> Configure, Type[] UnmappedEntities);

/// <summary>The application's own module: ABP's EF Core and DDD application modules, Autofac, and the harness' database.</summary>
[DependsOn(typeof(AbpAutofacModule), typeof(AbpEntityFrameworkCoreModule), typeof(AbpDddApplicationModule))]
public sealed class AbpHarnessModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var settings = context.Services.GetSingletonInstance<AbpHarnessSettings>();
        Configure<AbpDbConnectionOptions>(options => options.ConnectionStrings.Default = "Data Source=:memory:");
        Configure<AbpDbContextOptions>(options => options.Configure(c => settings.Configure(c.DbContextOptions)));
        if (settings.UnmappedEntities.Length == 0)
        {
            return;
        }

        AbpHarnessDbContext.Entities = settings.UnmappedEntities;
        context.Services.AddAbpDbContext<AbpHarnessDbContext>();
        foreach (var entity in settings.UnmappedEntities)
        {
            AddRepository(context.Services, entity);
        }
    }

    // What AddDefaultRepositories registers for a DbSet entity, for an entity the corpus compiled without EF Core.
    private static void AddRepository(IServiceCollection services, Type entity)
    {
        var key = entity.GetInterfaces().First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntity<>)).GetGenericArguments()[0];
        var implementation = typeof(EfCoreRepository<,,>).MakeGenericType(typeof(AbpHarnessDbContext), entity, key);
        foreach (var service in new[] { typeof(IReadOnlyBasicRepository<>), typeof(IReadOnlyRepository<>), typeof(IBasicRepository<>), typeof(IRepository<>), typeof(IEfCoreRepository<>) })
        {
            services.AddTransient(service.MakeGenericType(entity), implementation);
        }

        foreach (var service in new[] { typeof(IReadOnlyBasicRepository<,>), typeof(IReadOnlyRepository<,>), typeof(IBasicRepository<,>), typeof(IRepository<,>), typeof(IEfCoreRepository<,>) })
        {
            services.AddTransient(service.MakeGenericType(entity, key), implementation);
        }
    }
}

/// <summary>The EF Core layer of a corpus folder that has only an ABP application layer: maps the folder's entities.</summary>
public sealed class AbpHarnessDbContext(DbContextOptions<AbpHarnessDbContext> options) : AbpDbContext<AbpHarnessDbContext>(options)
{
    /// <summary>The entities of the folder being run; folders run one at a time.</summary>
    public static Type[] Entities { get; set; } = [];

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        foreach (var entity in Entities)
        {
            modelBuilder.Entity(entity, b => b.ConfigureByConvention());
        }
    }
}
