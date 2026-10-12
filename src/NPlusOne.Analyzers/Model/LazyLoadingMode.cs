namespace NPlusOne.Analyzers.Model;

/// <summary>Whether reading a navigation can run a query.</summary>
internal enum LazyLoadingMode
{
    /// <summary>Decided from the compilation: on when it can use lazy-loading proxies or an entity takes a lazy loader.</summary>
    Auto,

    /// <summary>Always on, for projects whose context configuration the analyzer cannot see.</summary>
    Enabled,

    /// <summary>Always off.</summary>
    Disabled,
}
