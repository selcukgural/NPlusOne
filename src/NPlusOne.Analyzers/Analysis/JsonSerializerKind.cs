namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// Which JSON serializer walks a value. Each honours only its own <c>[JsonIgnore]</c>, so whether a navigation is
/// read depends on it.
/// </summary>
internal enum JsonSerializerKind
{
    /// <summary>System.Text.Json: <c>JsonSerializer</c>, and what ASP.NET Core and SignalR use by default.</summary>
    SystemTextJson,

    /// <summary>Newtonsoft.Json: <c>JsonConvert</c>.</summary>
    Newtonsoft,
}
