using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers;

/// <summary>
/// The rules the analyzer reports. Messages are built by <see cref="Analysis.Messages"/>; each
/// rule's page in <c>docs/rules</c> explains what it detects, how to fix it and what it cannot see.
/// </summary>
internal static class Descriptors
{
    /// <summary>The category every rule belongs to, so a project can configure them together.</summary>
    private const string Category = "Performance";

    /// <summary>Where the rule pages live; the id and <c>.md</c> are appended.</summary>
    private const string HelpBase = "https://github.com/selcukgural/NPlusOne/blob/main/docs/rules/";

    /// <summary>A query runs once per element of a collection.</summary>
    public static readonly DiagnosticDescriptor QueryPerElement = Create(
        "NP0001",
        "A query runs once per element (N+1 read)",
        DiagnosticSeverity.Warning,
        "Each element of the collection costs one more database round trip. Load the data for all elements in one query before the loop.");

    /// <summary>A lazy-loaded navigation is read once per element of a collection.</summary>
    public static readonly DiagnosticDescriptor LazyLoadPerElement = Create(
        "NP0002",
        "A lazy-loaded navigation is read once per element",
        DiagnosticSeverity.Warning,
        "With lazy loading on, reading a navigation that the query did not load runs one query per element. Include the navigation or project the needed columns.");

    /// <summary><c>SaveChanges</c> runs once per element of a collection.</summary>
    public static readonly DiagnosticDescriptor SavePerElement = Create(
        "NP0003",
        "SaveChanges runs once per element (N+1 write)",
        DiagnosticSeverity.Warning,
        "Each SaveChanges call is a round trip and a transaction. Call it once after the loop, or use one set-based ExecuteUpdate or ExecuteDelete.");

    /// <summary>A method that runs a query calls itself, so a tree costs one round trip per level.</summary>
    public static readonly DiagnosticDescriptor RecursiveQuery = Create(
        "NP0004",
        "A recursive method runs a query on every call",
        DiagnosticSeverity.Warning,
        "Each level of the recursion is one more database round trip. Load the whole tree in one query and link it in memory, or use a recursive common table expression.");

    /// <summary>An endpoint returns entities whose lazy navigations the serializer loads one by one.</summary>
    public static readonly DiagnosticDescriptor SerializedLazyEntities = Create(
        "NP0005",
        "An endpoint returns entities whose lazy navigations the serializer loads one by one",
        DiagnosticSeverity.Warning,
        "The serializer reads every navigation of every returned entity; with lazy loading on, each read that was not included is one more query. Return DTOs projected with Select.");

    /// <summary>A method that looks like data access, but whose code the analyzer cannot see, is called once per element.</summary>
    public static readonly DiagnosticDescriptor PossibleQueryPerElement = Create(
        "NP0006",
        "A data-access abstraction is called once per element and its implementation is not visible",
        DiagnosticSeverity.Info,
        "The method looks like data access but its implementation is in an assembly the analyzer cannot see. Add a method that loads all elements at once, or declare it in nplusone.query_methods or nplusone.ignored_methods.");

    /// <summary>A mapper's naming conventions read lazy navigations of each element, and its configuration is not visible.</summary>
    public static readonly DiagnosticDescriptor ConventionMappingPerElement = Create(
        "NP0007",
        "A mapper reads lazy navigations of each element by naming convention",
        DiagnosticSeverity.Info,
        "AutoMapper and Mapster flatten members such as CustomerName from Customer.Name. With lazy loading on, that is one query per element for each navigation the query did not include. The mapping configuration is not visible here, so this is based on the naming conventions.");

    /// <summary>Builds a descriptor whose help link points at the rule's page.</summary>
    /// <param name="id">The rule id, such as NP0001.</param>
    /// <param name="title">A short title.</param>
    /// <param name="severity">The default severity.</param>
    /// <param name="description">A longer description shown in the IDE.</param>
    private static DiagnosticDescriptor Create(string id, string title, DiagnosticSeverity severity, string description)
        => new(id, title, "{0}", Category, severity, isEnabledByDefault: true, description, HelpBase + id + ".md");
}
