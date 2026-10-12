using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NPlusOne.Analyzers.Tests.Harness.GroundTruth;

/// <summary>A registered context type, so entity arguments can be loaded from the right context.</summary>
public sealed record ContextMarker(Type Type);

/// <summary>
/// Supplies the values a corpus constructor or method asks for, the way its callers would: id lists hold
/// the seeded ids (<c>count</c> of them), entities and entity lists are loaded and tracked by the scope's
/// context (or are new instances when the method adds what it is given; constructor arguments are loaded
/// by another context, as long-lived data the method's unit of work does not track), services come from the
/// container, SignalR clients are fakes that serialize, and other corpus types (requests, records) are
/// built from their constructors the same way.
/// </summary>
public sealed class Arguments(int count, IServiceProvider services, bool newEntities)
{
    private const int MaxDepth = 3;

    /// <summary>A value of the type, or null when none can be made.</summary>
    public object? For(Type type) => For(type, MaxDepth, detached: false);

    /// <summary>An instance built from the type's largest public constructor, or null.</summary>
    public object? Create(Type type) => Create(type, MaxDepth);

    private object? For(Type type, int depth, bool detached)
    {
        var ids = Enumerable.Range(1, count);
        return type switch
        {
            _ when type == typeof(CancellationToken) => new CancellationTokenSource(TimeSpan.FromMilliseconds(300)).Token,
            _ when type == typeof(int) || type == typeof(int?) => 1,
            _ when type == typeof(long) => 1L,
            _ when type == typeof(decimal) => 1m,
            _ when type == typeof(string) => "Name1",
            _ when type == typeof(bool) => false,
            _ when type == typeof(int[]) => ids.ToArray(),
            _ when type.IsAssignableFrom(typeof(List<int>)) => ids.ToList(),
            _ when type == typeof(HashSet<int>) => ids.ToHashSet(),
            _ when type == typeof(Queue<int>) => new Queue<int>(ids),
            _ when type == typeof(Stack<int>) => new Stack<int>(ids),
            _ when type == typeof(IAsyncEnumerable<int>) => Stream(ids),
            _ when type == typeof(Stream) => new MemoryStream(),
            _ => Entities(type, detached) ?? Service(type) ?? (depth > 0 && IsBuildable(type) ? Create(type, depth - 1) : null),
        };
    }

    private object? Create(Type type, int depth)
    {
        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (constructor is null)
        {
            return null;
        }

        var values = new object?[constructor.GetParameters().Length];
        foreach (var parameter in constructor.GetParameters())
        {
            values[parameter.Position] = For(parameter.ParameterType, depth, detached: true);
            if (values[parameter.Position] is null)
            {
                return null;
            }
        }

        var instance = constructor.Invoke(values);

        // Hub<T> hides Hub.Clients with its typed one; both get a fake.
        foreach (var clients in instance is Hub ? type.GetProperties().Where(p => p is { Name: "Clients", CanWrite: true }) : [])
        {
            clients.SetValue(instance, SignalRFake.Create(clients.PropertyType));
        }

        return instance;
    }

    private static bool IsBuildable(Type type) => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && type.Assembly.IsDynamic is false && type.Namespace?.StartsWith("Corpus", StringComparison.Ordinal) == true;

    private object? Service(Type type)
    {
        try
        {
            return services.GetService(type) ?? (SignalRFake.Fakes(type) ? SignalRFake.Create(type) : null);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // An entity, a list of them or a query over them, as a caller would pass it: loaded and tracked by
    // the scope's context, or new unsaved instances when the method adds what it is given.
    private object? Entities(Type type, bool detached)
    {
        var source = detached ? services.GetRequiredService<IServiceScopeFactory>().CreateScope().ServiceProvider : services;
        var queryable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>);
        var element = type.IsArray ? type.GetElementType()
            : type.IsGenericType && type.GetGenericArguments().Length == 1 && (queryable || type.IsAssignableFrom(typeof(List<>).MakeGenericType(type.GetGenericArguments()[0]))) ? type.GetGenericArguments()[0]
            : type;
        foreach (var contextType in services.GetServices<ContextMarker>().Select(m => m.Type))
        {
            var context = (DbContext)source.GetRequiredService(contextType);
            if (element is null || context.Model.FindEntityType(element) is null)
            {
                continue;
            }

            var set = (IQueryable<object>)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(element).Invoke(context, null)!;
            if (queryable)
            {
                return set;
            }

            var loaded = newEntities
                ? [.. Enumerable.Range(0, count).Select(_ => Activator.CreateInstance(element)!)]
                : set.ToList();
            if (element == type)
            {
                return loaded.FirstOrDefault();
            }

            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            foreach (var entity in loaded)
            {
                list.Add(entity);
            }

            if (!type.IsArray)
            {
                return list;
            }

            var array = Array.CreateInstance(element, list.Count);
            list.CopyTo(array, 0);
            return array;
        }

        return null;
    }

    private static async IAsyncEnumerable<int> Stream(IEnumerable<int> ids)
    {
        foreach (var id in ids)
        {
            await Task.Yield();
            yield return id;
        }
    }
}
