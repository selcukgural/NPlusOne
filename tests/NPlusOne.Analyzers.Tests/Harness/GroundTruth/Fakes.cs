using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace NPlusOne.Analyzers.Tests.Harness.GroundTruth;

/// <summary>
/// Serializes values the way ASP.NET Core and SignalR do (System.Text.Json, cycles ignored), so lazy
/// navigations of returned or sent entities load exactly as they would in a real response.
/// </summary>
public static class ResponseSerializer
{
    private static readonly JsonSerializerOptions Options = new() { ReferenceHandler = ReferenceHandler.IgnoreCycles, MaxDepth = 64 };

    public static async Task SerializeAsync(object? value, Type? declared = null)
    {
        if (value is null)
        {
            return;
        }

        // SignalR streams a ChannelReader<T> a hub method returns item by item.
        for (var type = value.GetType(); type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Threading.Channels.ChannelReader<>))
            {
                var stream = typeof(ResponseSerializer).GetMethod(nameof(SerializeChannelAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
                await (Task)stream.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, [value])!;
                return;
            }
        }

        await JsonSerializer.SerializeAsync(Stream.Null, value, SerializedType(value, declared), Options);
    }

    // MVC's Newtonsoft.Json formatter, with reference loops ignored as an application serializing entities must set.
    public static void SerializeWithNewtonsoft(object? value)
    {
        if (value is not null)
        {
            Newtonsoft.Json.JsonConvert.SerializeObject(value, new Newtonsoft.Json.JsonSerializerSettings { ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore });
        }
    }

    private static async Task SerializeChannelAsync<T>(System.Threading.Channels.ChannelReader<T> reader)
    {
        await foreach (var item in reader.ReadAllAsync())
        {
            await SerializeAsync(item, typeof(T));
        }
    }

    // A query or stream is serialized as the sequence the method declares, not as EF Core's runtime query type.
    private static Type SerializedType(object value, Type? declared)
        => declared is { IsGenericType: true } generic
           && (generic.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)
               || generic.GetGenericTypeDefinition() == typeof(IEnumerable<>)
               || generic.GetGenericTypeDefinition() == typeof(IQueryable<>))
            ? declared
            : value.GetType();
}

/// <summary>
/// Stands in for SignalR's hub clients and hub contexts: every interface it hands out is another fake,
/// and every call that sends (returns a task) serializes its arguments like SignalR's protocol would.
/// </summary>
public class SignalRFake : DispatchProxy
{
    public static bool Fakes(Type type) => type.IsInterface && type.Namespace == "Microsoft.AspNetCore.SignalR";

    public static object Create(Type interfaceType) => Create(interfaceType, typeof(SignalRFake));

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var returnType = targetMethod!.ReturnType;
        if (returnType.IsInterface)
        {
            return Create(returnType);
        }

        if (!typeof(Task).IsAssignableFrom(returnType))
        {
            return null;
        }

        return SendAsync(args ?? []);
    }

    private static async Task SendAsync(object?[] args)
    {
        foreach (var argument in args)
        {
            if (argument is object?[] many)
            {
                foreach (var item in many)
                {
                    await ResponseSerializer.SerializeAsync(item);
                }
            }
            else if (argument is not CancellationToken and not string)
            {
                await ResponseSerializer.SerializeAsync(argument);
            }
        }
    }
}

/// <summary>
/// The source-generated Mediator library's <c>IMediator</c> without its generator: <c>Send</c> runs the
/// corpus handler for the message's type, <c>Publish</c> every notification handler.
/// </summary>
public class MediatorFake : DispatchProxy
{
    private IReadOnlyList<Assembly> _corpus = null!;
    private IServiceProvider _services = null!;

    public static object Create(Type interfaceType, IReadOnlyList<Assembly> corpus, IServiceProvider services)
    {
        var proxy = (MediatorFake)Create(interfaceType, typeof(MediatorFake));
        proxy._corpus = corpus;
        proxy._services = services;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var message = args![0]!;
        var publish = targetMethod!.Name == "Publish";
        var handlers = new List<object>();
        foreach (var type in _corpus.SelectMany(a => a.GetTypes()))
        {
            if (type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            foreach (var iface in type.GetInterfaces())
            {
                if (iface.Namespace == "Mediator" && iface.IsGenericType && Handles(iface.Name, publish) && iface.GetGenericArguments()[0].IsInstanceOfType(message))
                {
                    var handle = iface.GetMethod("Handle") ?? iface.GetInterfaces().Select(i => i.GetMethod("Handle")).First(m => m is not null)!;
                    handlers.Add(handle.Invoke(ActivatorUtilities.CreateInstance(_services, type), [message, CancellationToken.None])!);
                    break;
                }
            }
        }

        if (!publish)
        {
            return handlers.Single();
        }

        return new ValueTask(Task.WhenAll(handlers.Select(h => ((ValueTask)h).AsTask())));
    }

    private static bool Handles(string handlerInterface, bool publish)
        => publish
            ? handlerInterface.StartsWith("INotificationHandler", StringComparison.Ordinal)
            : handlerInterface.StartsWith("IRequestHandler", StringComparison.Ordinal)
              || handlerInterface.StartsWith("ICommandHandler", StringComparison.Ordinal)
              || handlerInterface.StartsWith("IQueryHandler", StringComparison.Ordinal);
}

/// <summary>
/// Implements a corpus interface whose implementation the corpus does not contain (a repository in a
/// project it does not reference): every call returns a default value or a completed task, and runs no SQL.
/// </summary>
public class NullFake : DispatchProxy
{
    public static object Create(Type interfaceType) => Create(interfaceType, typeof(NullFake));

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var type = targetMethod!.ReturnType;
        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var value = Default(type.GetGenericArguments()[0]);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, [value]);
        }

        return Default(type);
    }

    private static object? Default(Type type) => type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
}
