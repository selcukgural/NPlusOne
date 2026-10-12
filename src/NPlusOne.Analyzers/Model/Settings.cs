using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NPlusOne.Analyzers.Model;

/// <summary>
/// The analyzer's options for one compilation, read from <c>.globalconfig</c> or, for keys a global
/// file does not set, from the <c>.editorconfig</c> section that applies to the project's C# files.
/// </summary>
/// <param name="LazyLoading">Whether navigations load lazily (<c>nplusone.lazy_loading</c>).</param>
/// <param name="MaxCallDepth">How many calls deep summaries follow (<c>nplusone.max_call_depth</c>).</param>
/// <param name="QueryMethods">Methods declared to run a query on every call (<c>nplusone.query_methods</c>).</param>
/// <param name="IgnoredMethods">Methods declared never to run a query (<c>nplusone.ignored_methods</c>).</param>
/// <param name="IteratingMethods">Methods declared to call their delegate once per element (<c>nplusone.iterating_methods</c>).</param>
/// <param name="DataAccessTypes">Type name patterns NP0006 treats as data access (<c>nplusone.data_access_types</c>).</param>
internal sealed record Settings(
    LazyLoadingMode LazyLoading,
    int MaxCallDepth,
    NamePatterns QueryMethods,
    NamePatterns IgnoredMethods,
    NamePatterns IteratingMethods,
    NamePatterns DataAccessTypes)
{
    /// <summary>The default depth: deep enough for controller, service, repository and a helper.</summary>
    private const int DefaultMaxCallDepth = 4;

    /// <summary>The naming conventions of repositories and query objects in .NET code bases.</summary>
    private const string DefaultDataAccessTypes = "*Repository;*Repo;*Store;*Dao;*DataAccess;*QueryService;*Queries";

    /// <summary>Reads the settings for a compilation.</summary>
    /// <param name="provider">The analyzer config options of the compilation.</param>
    /// <param name="compilation">The compilation, whose first source file's options fill keys no global file sets.</param>
    public static Settings Read(AnalyzerConfigOptionsProvider provider, Compilation compilation)
    {
        var global = provider.GlobalOptions;
        var firstTree = compilation.SyntaxTrees.FirstOrDefault();
        var perFile = firstTree is null ? null : provider.GetOptions(firstTree);

        string? Get(string key)
        {
            if (global.TryGetValue(key, out var value))
            {
                return value;
            }

            return perFile is not null && perFile.TryGetValue(key, out value) ? value : null;
        }

        var lazy = Get("nplusone.lazy_loading")?.Trim().ToLowerInvariant() switch
        {
            "enabled" or "true" or "on" => LazyLoadingMode.Enabled,
            "disabled" or "false" or "off" => LazyLoadingMode.Disabled,
            _ => LazyLoadingMode.Auto,
        };

        var depth = int.TryParse(Get("nplusone.max_call_depth"), out var d) && d is >= 0 and <= 16 ? d : DefaultMaxCallDepth;

        return new Settings(
            lazy,
            depth,
            NamePatterns.Parse(Get("nplusone.query_methods")),
            NamePatterns.Parse(Get("nplusone.ignored_methods")),
            NamePatterns.Parse(Get("nplusone.iterating_methods")),
            NamePatterns.Parse(Get("nplusone.data_access_types") ?? DefaultDataAccessTypes));
    }
}
