using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NPlusOne.EntityFrameworkCore.Recording;

/// <summary>
/// Counts lazy loads by listening to EF Core's <see cref="DiagnosticListener"/> for
/// <see cref="CoreEventId.NavigationLazyLoading"/>. EF Core raises the event from its lazy loader
/// (proxies and injected <c>ILazyLoader</c> alike) right before it loads the navigation, on the
/// thread that read the property, so the async-local scope and the call site are both available.
/// </summary>
/// <remarks>
/// A diagnostic listener needs no change to the DbContext, does not compete with the application's
/// <c>LogTo</c> or logger factory, and works the same on EF Core 8 and 10. EF Core asks
/// <see cref="IsEnabled"/> before it builds the event payload, so outside a scope the event costs
/// one async-local read. The subscription lives as long as the process.
/// </remarks>
internal sealed class LazyLoadListener : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    /// <summary>The diagnostic event name EF Core uses for a lazy load.</summary>
    private static readonly string EventName = CoreEventId.NavigationLazyLoading.Name!;

    /// <summary>The single listener instance.</summary>
    private static readonly LazyLoadListener Instance = new();

    /// <summary>
    /// The <c>Entity.Navigation</c> name of each navigation lazy-loaded so far, per entity type: a lazy load in a loop
    /// happens once per element, and its name is the same each time. Keyed weakly by the type, so a collectible assembly
    /// can still be unloaded.
    /// </summary>
    private static readonly ConditionalWeakTable<Type, ConcurrentDictionary<string, string>> NavigationNames = [];

    /// <summary>1 once subscribed to <see cref="DiagnosticListener.AllListeners"/>.</summary>
    private static int _subscribed;

    /// <summary>Subscribes once per process; later calls return immediately.</summary>
    public static void EnsureSubscribed()
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 1)
        {
            return;
        }

        _ = DiagnosticListener.AllListeners.Subscribe(Instance);
    }

    /// <summary>Subscribes to each EF Core listener (one per internal service provider) as it appears.</summary>
    /// <param name="value">A diagnostic listener created in the process.</param>
    public void OnNext(DiagnosticListener value)
    {
        if (value.Name != DbLoggerCategory.Name)
        {
            return;
        }

        _ = value.Subscribe(this, IsEnabled);
    }

    /// <summary>Records a lazy load into the current scope.</summary>
    /// <param name="value">The event name and payload.</param>
    public void OnNext(KeyValuePair<string, object?> value)
    {
        // Other subscribers may enable more events; every event reaches every observer.
        if (value.Key != EventName || value.Value is not LazyLoadingEventData data)
        {
            return;
        }

        var scope = NPlusOneScope.Current;
        if (scope is null || !ContextFacts.Of(data.Context).UsesGuard)
        {
            return;
        }

        // EF Core calls observers synchronously from the lazy load: recording must never break it.
        try
        {
            var entityType = ResolveEntityType(data);
            NPlusOneScope.RecordLazyLoad(scope, NavigationName(entityType, data.NavigationPropertyName), entityType);
        }
#pragma warning disable CA1031 // A failure to record is dropped, not thrown into the application.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>The <c>Entity.Navigation</c> name of a navigation, built once per entity type and navigation.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="navigation">The navigation's name.</param>
    /// <returns>The name.</returns>
    internal static string NavigationName(Type entityType, string navigation)
    {
        var names = NavigationNames.GetValue(entityType, static _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
        return names.TryGetValue(navigation, out var name) ? name : names.GetOrAdd(navigation, string.Concat(entityType.Name, ".", navigation));
    }

    /// <summary>Not used: the listener streams never complete in a way that matters to the guard.</summary>
    public void OnCompleted()
    {
    }

    /// <summary>Not used: diagnostic listeners do not report errors to observers.</summary>
    /// <param name="error">The error.</param>
    public void OnError(Exception error)
    {
    }

    /// <summary>Asks EF Core to raise only the lazy-load event, and only while a scope is active.</summary>
    /// <param name="name">The event name.</param>
    /// <returns>Whether EF Core should build and send the payload.</returns>
    private static bool IsEnabled(string name) => NPlusOneScope.Current is not null && name == EventName;

    /// <summary>
    /// Finds the mapped entity type for a lazy-loaded entity, so proxies (<c>Castle.Proxies.OrderProxy</c>)
    /// are reported under the entity name (<c>Order</c>).
    /// </summary>
    /// <param name="data">The event payload.</param>
    /// <returns>The entity CLR type.</returns>
    private static Type ResolveEntityType(LazyLoadingEventData data)
    {
        var runtimeType = data.Entity.GetType();
        var mapped = data.Context?.Model.FindRuntimeEntityType(runtimeType)?.ClrType;
        if (mapped is not null)
        {
            return mapped;
        }

        while (runtimeType.Namespace == "Castle.Proxies" && runtimeType.BaseType is { } baseType)
        {
            runtimeType = baseType;
        }

        return runtimeType;
    }
}
