using System.Reflection;
using System.Runtime.Loader;
using Mapster;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NPlusOne.Analyzers.Tests.Harness.GroundTruth;

/// <summary>
/// What one run of a corpus method did: the SQL commands it sent, or why it could not run; under the runtime guard, the
/// suspects the guard reported.
/// </summary>
public sealed record Measurement(IReadOnlyList<string> Commands, string? NotRun, IReadOnlyList<string>? Suspects = null)
{
    public int Count => Commands.Count;
}

/// <summary>
/// Compiles a corpus folder, loads it, and runs its methods against a real SQLite database (an
/// in-memory connection per context, lazy-loading proxies when the folder has lazy loading on), seeded
/// with a chosen number of rows per entity. Constructor and method parameters come from a service
/// container holding the corpus' own services, the real MediatR, AutoMapper and Mapster, and fakes for
/// the source-generated Mediator and SignalR; id lists get the seeded ids. An endpoint's result (an MVC
/// action, a hub method, a minimal API handler) is serialized as ASP.NET Core would.
/// </summary>
public sealed class CorpusRuntime
{
    /// <summary>The test collection of every test that runs the corpus, so two never load the same folder at once.</summary>
    public const string Collection = "Corpus runtime";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly CorpusLoadContext _loadContext;
    private readonly IReadOnlyList<Assembly> _assemblies;
    private readonly bool _proxies;
    private readonly bool _newtonsoftMvc;
    private readonly IRuntimeDetector? _detector;
    private readonly Type[] _abpModules;
    private readonly Type[] _abpEntities;

    /// <param name="folder">The corpus folder.</param>
    /// <param name="detector">A runtime N+1 detector to attach to the contexts and wrap every run in, or none.</param>
    public CorpusRuntime(string folder, IRuntimeDetector? detector = null)
    {
        _detector = detector;
        var options = Corpus.Options(folder);
        var excluded = options.TryGetValue("harness.exclude_references", out var list)
            ? list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        // Every folder's project is called Corpus; a unique name keeps name-based type lookups (DispatchProxy resolves the
        // interfaces it implements by assembly name) in the right folder, and in the right one of two runs of a folder.
        var prefix = folder + (detector is null ? "." : "." + detector.Key + ".");
        var emitted = TestCompilation.Emit([.. Corpus.Projects(folder).Select(p => p with { Name = prefix + p.Name })], excluded);
        _loadContext = new CorpusLoadContext(folder);
        _assemblies = [.. emitted.Select(e => _loadContext.LoadFromStream(new MemoryStream(e.Image)))];
        Methods = CaseMethod.Collect(emitted.SelectMany(e => e.Trees));

        // A folder that switches MVC to Newtonsoft.Json (AddNewtonsoftJson) has its controllers' results written by it.
        _newtonsoftMvc = emitted.SelectMany(e => e.Trees).Any(t => t.GetText().ToString().Contains("AddNewtonsoftJson", StringComparison.Ordinal));
        // An ABP folder runs under ABP; one that is only an application layer has its entities mapped by the harness, and
        // the lazy loading its settings declare is turned on in that EF Core layer it does not compile against.
        _abpModules = AbpHarness.Modules(_assemblies);
        _abpEntities = _abpModules.Length > 0 ? AbpHarness.UnmappedEntities(_assemblies) : [];
        var lazy = options.TryGetValue("nplusone.lazy_loading", out var setting) ? setting : null;
        _proxies = lazy != "disabled" && (!excluded.Contains("Microsoft.EntityFrameworkCore.Proxies") || (_abpEntities.Length > 0 && lazy == "enabled"));
        TypeAdapterConfig.GlobalSettings.Scan([.. _assemblies]);
    }

    /// <summary>The methods declared in the folder's sources.</summary>
    public IReadOnlyList<CaseMethod> Methods { get; }

    /// <summary>Runs one method on a database seeded with <paramref name="count"/> rows per entity.</summary>
    public async Task<Measurement> RunAsync(CaseMethod caseMethod, int count)
    {
        var method = Find(caseMethod);
        if (method is null)
        {
            return new Measurement([], "not found by reflection");
        }

        try
        {
            return await Task.Run(() => RunCoreAsync(caseMethod, method, count)).WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
            return new Measurement([], "timed out");
        }
    }

    private MethodInfo? Find(CaseMethod caseMethod)
    {
        foreach (var assembly in _assemblies)
        {
            if (assembly.GetType(caseMethod.TypeName) is not { IsGenericTypeDefinition: false } type)
            {
                continue;
            }

            return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == caseMethod.Name && m.GetParameters().Length == caseMethod.ParameterCount && !m.IsGenericMethodDefinition && !m.IsAbstract);
        }

        return null;
    }

    private async Task<Measurement> RunCoreAsync(CaseMethod caseMethod, MethodInfo method, int count)
    {
        using var reflection = _loadContext.EnterContextualReflection();
        var log = new CommandLog();
        var connections = new List<SqliteConnection>();
        var detection = _detector?.NewRun();
        try
        {
            var collection = caseMethod.Route is null ? new ServiceCollection() : null;
            var builder = caseMethod.Route is null ? null : WebApplication.CreateSlimBuilder();
            ConfigureServices(collection ?? builder!.Services, log, connections, detection);
            builder?.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles);
            var app = builder?.Build();
            Volo.Abp.IAbpApplicationWithExternalServiceProvider? abp = null;
            IServiceProvider provider;
            if (_abpModules.Length > 0 && collection is not null)
            {
                var connection = Open(connections);
                var settings = new AbpHarnessSettings(b => ConfigureContext(b, connection, log, detection), _abpEntities);
                (abp, provider) = await AbpHarness.StartAsync(collection, _abpModules, settings);
            }
            else
            {
                provider = app?.Services ?? collection!.BuildServiceProvider();
            }

            await using var owner = (IAsyncDisposable?)app ?? (IAsyncDisposable)provider;
            using var application = abp;
            using (var seeding = provider.CreateScope())
            {
                foreach (var contextType in ContextTypes())
                {
                    var context = (DbContext)seeding.ServiceProvider.GetRequiredService(contextType);
                    context.Database.EnsureCreated();
                    Seeder.Seed(context, count);
                }

                if (abp is not null)
                {
                    using var seedingWork = AbpHarness.Begin(seeding.ServiceProvider);
                    foreach (var contextType in AbpContextTypes())
                    {
                        var context = await AbpHarness.ContextAsync(seeding.ServiceProvider, contextType);
                        context.Database.EnsureCreated();
                        Seeder.Seed(context, count);
                    }

                    await seedingWork.CompleteAsync();
                }
            }

            using var scope = provider.CreateScope();
            detection?.Begin();
            if (app is not null)
            {
                return WithSuspects(await RunEndpointAsync(caseMethod, method, app, scope.ServiceProvider, log), detection);
            }

            // Under ABP the method runs in one unit of work, as an application service call does; its end saves what the
            // method left unsaved, which counts as the method's.
            using var work = abp is null ? null : AbpHarness.Begin(scope.ServiceProvider);
            var arguments = new Arguments(count, scope.ServiceProvider, caseMethod.NewEntities);
            var instance = method.IsStatic ? null
                : abp is not null && AbpHarness.IsAbpService(method.DeclaringType!)
                    ? scope.ServiceProvider.GetService(method.DeclaringType!)
                    : arguments.Create(method.DeclaringType!);
            if (!method.IsStatic && instance is null)
            {
                return new Measurement([], $"cannot create {method.DeclaringType!.Name}");
            }

            var values = new object?[method.GetParameters().Length];
            foreach (var parameter in method.GetParameters())
            {
                values[parameter.Position] = arguments.For(parameter.ParameterType);
                if (values[parameter.Position] is null)
                {
                    return new Measurement([], $"no value for parameter {parameter.ParameterType.Name} {parameter.Name}");
                }
            }

            log.Recording = true;
            var result = await AwaitAsync(method.Invoke(instance, values));
            if (IsEndpoint(caseMethod, method) && _newtonsoftMvc && typeof(ControllerBase).IsAssignableFrom(method.DeclaringType))
            {
                ResponseSerializer.SerializeWithNewtonsoft(EndpointValue(result));
            }
            else if (IsEndpoint(caseMethod, method))
            {
                await ResponseSerializer.SerializeAsync(EndpointValue(result), DeclaredResult(method.ReturnType));
            }
            else
            {
                await EnumerateDeferredAsync(result);
            }

            if (work is not null)
            {
                await work.CompleteAsync();
            }

            log.Recording = false;
            return WithSuspects(new Measurement([.. log.Commands], null), detection);
        }
        // A cancelled poll ends normally.
        catch (Exception e) when (Unwrap(e) is OperationCanceledException)
        {
            return new Measurement([.. log.Commands], null);
        }
        catch (Exception e)
        {
            var inner = Unwrap(e);
            var detail = inner is DbUpdateException { InnerException: { } cause } ? " " + cause.Message.Split('\n')[0] : "";
            return new Measurement([], $"throws {inner.GetType().Name}: {inner.Message.Split('\n')[0]}{detail}");
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }

    // A minimal API lambda: the mapping method runs on a real web application (with JSON cycles ignored,
    // as an application serializing entities must configure), and the endpoint is called with a request
    // whose services are the scope's; ASP.NET Core binds the handler's parameters and writes its result.
    private static async Task<Measurement> RunEndpointAsync(CaseMethod caseMethod, MethodInfo map, WebApplication app, IServiceProvider services, CommandLog log)
    {
        map.Invoke(null, [app]);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        var endpoint = endpoints.FirstOrDefault(e => e.RoutePattern.RawText == caseMethod.Route)
                       ?? endpoints.FirstOrDefault(e => e.RoutePattern.RawText?.EndsWith(caseMethod.Route!, StringComparison.Ordinal) == true);
        if (endpoint?.RequestDelegate is null)
        {
            return new Measurement([], $"no endpoint for {caseMethod.Route}");
        }

        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        log.Recording = true;
        await endpoint.RequestDelegate(context);
        log.Recording = false;
        return new Measurement([.. log.Commands], null);
    }

    // What the detector reported, when the run happened under one.
    private static Measurement WithSuspects(Measurement measurement, IDetectorRun? detection)
        => detection is null ? measurement : measurement with { Suspects = detection.Finish() };

    private void ConfigureServices(IServiceCollection services, CommandLog log, List<SqliteConnection> connections, IDetectorRun? detection)
    {
        services.AddLogging();
        foreach (var contextType in ContextTypes())
        {
            var connection = Open(connections);
            services.AddSingleton(new ContextMarker(contextType));
            AddContext(services, contextType, builder => ConfigureContext(builder, connection, log, detection));
        }

        // ABP creates and configures its contexts itself (AbpHarness); entity arguments are loaded from them all the same.
        foreach (var contextType in _abpModules.Length > 0 ? AbpContextTypes() : [])
        {
            services.AddSingleton(new ContextMarker(contextType));
        }

        // A corpus method that takes MediatR's configuration registers what the corpus registers (pipeline behaviors).
        services.AddMediatR(c =>
        {
            c.RegisterServicesFromAssemblies([.. _assemblies]);
            foreach (var setup in _assemblies.SelectMany(a => a.GetTypes()).SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                         .Where(m => m.GetParameters() is [{ ParameterType: var type }] && type == typeof(MediatRServiceConfiguration)))
            {
                setup.Invoke(null, [c]);
            }
        });
        services.AddAutoMapper(_ => { }, [.. _assemblies]);
        services.AddSingleton<MapsterMapper.IMapper>(new MapsterMapper.Mapper(TypeAdapterConfig.GlobalSettings));
        foreach (var iface in _assemblies.SelectMany(a => a.GetTypes()).Where(t => t.IsInterface && !t.IsGenericTypeDefinition))
        {
            if (!_assemblies.SelectMany(a => a.GetTypes()).Any(t => t.IsClass && !t.IsAbstract && iface.IsAssignableFrom(t)))
            {
                services.AddSingleton(iface, NullFake.Create(iface));
            }
        }

        foreach (var mediator in new[] { typeof(Mediator.IMediator), typeof(Mediator.ISender), typeof(Mediator.IPublisher) })
        {
            services.AddScoped(mediator, sp => MediatorFake.Create(mediator, _assemblies, sp));
        }

        foreach (var type in _assemblies.SelectMany(a => a.GetTypes()))
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition || typeof(DbContext).IsAssignableFrom(type) || type.IsNested && type.IsNestedPrivate
                || typeof(Volo.Abp.Modularity.AbpModule).IsAssignableFrom(type))
            {
                continue;
            }

            services.TryAddScoped(type);
            for (var baseType = type.BaseType; baseType is not null && _assemblies.Contains(baseType.Assembly); baseType = baseType.BaseType)
            {
                services.AddScoped(baseType, type);
            }

            foreach (var iface in type.GetInterfaces())
            {
                if (_assemblies.Contains(iface.Assembly) && !iface.IsGenericTypeDefinition)
                {
                    services.AddScoped(iface, type);
                }
            }
        }

    }

    // The plain contexts, which the harness registers; ABP's are AbpContextTypes.
    private IEnumerable<Type> ContextTypes()
        => _assemblies.SelectMany(a => a.GetTypes()).Where(t => typeof(DbContext).IsAssignableFrom(t) && !t.IsAbstract && !AbpHarness.IsAbpContext(t));

    // The folder's ABP contexts, or the harness' one that maps the entities of a folder without its own.
    private IEnumerable<Type> AbpContextTypes()
        => _abpEntities.Length > 0
            ? [typeof(AbpHarnessDbContext)]
            : _assemblies.SelectMany(a => a.GetTypes()).Where(t => !t.IsAbstract && AbpHarness.IsAbpContext(t));

    private static SqliteConnection Open(List<SqliteConnection> connections)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        connections.Add(connection);
        return connection;
    }

    private void ConfigureContext(DbContextOptionsBuilder builder, SqliteConnection connection, CommandLog log, IDetectorRun? detection)
    {
        // EF Core's internal services (proxy types, compiled models) are cached per options shape, which is the
        // same for every folder; each folder's Corpus.Order is a different type, so nothing is shared.
        builder.UseSqlite(connection).AddInterceptors(log).EnableServiceProviderCaching(false)
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer, DecimalsAsDoubles>();
        detection?.Configure(builder);
        if (_proxies)
        {
            builder.UseLazyLoadingProxies(o => o.IgnoreNonVirtualNavigations());
        }
    }

    // AddDbContextFactory<TContext>, with the context itself registered as scoped from the factory.
    private static void AddContext(IServiceCollection services, Type contextType, Action<DbContextOptionsBuilder> configure)
    {
        var addFactory = typeof(EntityFrameworkServiceCollectionExtensions).GetMethods()
            .Single(m => m.Name == "AddDbContextFactory" && m.GetGenericArguments().Length == 1
                         && m.GetParameters() is [_, { ParameterType: var p }, _] && p == typeof(Action<DbContextOptionsBuilder>));
        addFactory.MakeGenericMethod(contextType).Invoke(null, [services, configure, ServiceLifetime.Singleton]);
        var factoryType = typeof(IDbContextFactory<>).MakeGenericType(contextType);
        services.TryAdd(ServiceDescriptor.Scoped(contextType, sp => factoryType.GetMethod("CreateDbContext")!.Invoke(sp.GetRequiredService(factoryType), null)!));
    }

    private static async Task<object?> AwaitAsync(object? result)
    {
        switch (result)
        {
            case Task task:
                await task;
                var property = task.GetType().GetProperty("Result");
                return property is null || property.PropertyType.Name == "VoidTaskResult" ? null : property.GetValue(task);
            case ValueTask valueTask:
                await valueTask;
                return null;
            case not null when result.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(ValueTask<>):
                return await AwaitAsync(type.GetMethod("AsTask")!.Invoke(result, null));
            default:
                return result;
        }
    }

    // A caller enumerates what an iterator or a query returns; that is when it runs.
    private static async Task EnumerateDeferredAsync(object? result)
    {
        switch (result)
        {
            case null or string or System.Collections.ICollection:
                return;
            case IQueryable or System.Collections.IEnumerable when result.GetType().Name.Contains('<', StringComparison.Ordinal) || result is IQueryable:
                foreach (var item in (System.Collections.IEnumerable)result)
                {
                    _ = item;
                }

                return;
            case not null when result.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)) is { } stream:
                var enumerate = typeof(CorpusRuntime).GetMethod(nameof(EnumerateAsync), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(stream.GetGenericArguments()[0]);
                await (Task)enumerate.Invoke(null, [result])!;
                return;
        }
    }

    private static async Task EnumerateAsync<T>(IAsyncEnumerable<T> stream)
    {
        await foreach (var item in stream)
        {
            _ = item;
        }
    }

    private static bool IsEndpoint(CaseMethod caseMethod, MethodInfo method)
        => caseMethod.IsMinimalApiHandler
           || method is { IsPublic: true, IsStatic: false }
           && (typeof(ControllerBase).IsAssignableFrom(method.DeclaringType) && method.GetCustomAttribute<NonActionAttribute>() is null
               || typeof(Hub).IsAssignableFrom(method.DeclaringType));

    // What ASP.NET Core serializes from an endpoint's result: the value of an ObjectResult, a JsonResult,
    // an ActionResult<T> or a typed minimal API result; nothing for a view or a status code.
    private static object? EndpointValue(object? result) => result switch
    {
        ObjectResult objectResult => objectResult.Value,
        JsonResult jsonResult => jsonResult.Value,
        IActionResult => null,
        IValueHttpResult valueResult => valueResult.Value,
        Microsoft.AspNetCore.Mvc.Infrastructure.IConvertToActionResult convertible => EndpointValue(convertible.Convert()),
        _ => result,
    };

    private static Type DeclaredResult(Type type)
    {
        while (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>) || type.GetGenericTypeDefinition() == typeof(ActionResult<>)))
        {
            type = type.GetGenericArguments()[0];
        }

        return type;
    }

    private static Exception Unwrap(Exception e)
    {
        while (e is TargetInvocationException or AggregateException && e.InnerException is not null)
        {
            e = e.InnerException;
        }

        return e;
    }

    /// <summary>
    /// A load context per folder, so folders can reuse type names; the assemblies of a multi-project folder
    /// resolve each other. Not collectible: EF Core's compiled queries and Castle's proxies live in
    /// non-collectible assemblies, which may not reference collectible ones.
    /// </summary>
    private sealed class CorpusLoadContext(string name) : AssemblyLoadContext(name)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
            => Assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
    }
}
