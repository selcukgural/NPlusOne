using System.Collections.Immutable;

namespace NPlusOne.Analyzers.Tests.Harness;

/// <summary>
/// The corpus folders copied next to the test assembly: each folder but <c>Shared</c> is one
/// project, compiled with the files in <c>Shared</c> and the options in its <c>.globalconfig</c>.
/// </summary>
public static class Corpus
{
    /// <summary>The corpus root: <c>NPLUSONE_CORPUS</c> when set (for tools run elsewhere), else the copy in the test output directory.</summary>
    public static string Root => Environment.GetEnvironmentVariable("NPLUSONE_CORPUS") ?? Path.Combine(AppContext.BaseDirectory, "corpus");

    /// <summary>The folder names that are compiled as projects.</summary>
    public static IEnumerable<string> Folders()
        => Directory.GetDirectories(Root).Select(Path.GetFileName).Where(n => n != "Shared").Order(StringComparer.Ordinal)!;

    /// <summary>
    /// The projects of a corpus folder, in build order. A folder with a <c>projects</c> file lists its
    /// projects (subfolders) one per line, each referencing the ones before it, and the shared files go
    /// into the first; any other folder is a single project.
    /// </summary>
    public static IReadOnlyList<TestCompilation.Project> Projects(string folder)
    {
        var list = Path.Combine(Root, folder, "projects");
        if (!File.Exists(list))
        {
            return [new TestCompilation.Project("Corpus", Sources(folder))];
        }

        var names = File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return [.. names.Select((name, i) => new TestCompilation.Project(
            name,
            [.. (i == 0 ? SharedFiles(folder) : [])
                .Concat(Directory.GetFiles(Path.Combine(Root, folder, name), "*.cs", SearchOption.AllDirectories))
                .Order(StringComparer.Ordinal)
                .Select(p => new TestCompilation.Source(p, File.ReadAllText(p)))]))];
    }

    /// <summary>The sources of one corpus project: its own files and the shared ones.</summary>
    public static IReadOnlyList<TestCompilation.Source> Sources(string folder)
        => [.. SharedFiles(folder)
            .Concat(Directory.GetFiles(Path.Combine(Root, folder), "*.cs", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .Select(p => new TestCompilation.Source(p, File.ReadAllText(p)))];

    /// <summary>
    /// The shared files a folder compiles with: none when its <c>.globalconfig</c> sets <c>harness.shared = false</c>
    /// (a project that sees no EF Core, such as an ABP application layer, cannot compile the shared model).
    /// </summary>
    private static string[] SharedFiles(string folder)
        => Options(folder).TryGetValue("harness.shared", out var shared) && shared == "false"
            ? []
            : Directory.GetFiles(Path.Combine(Root, "Shared"), "*.cs");

    /// <summary>The folder's <c>.globalconfig</c> as key/value pairs (empty when it has none).</summary>
    public static ImmutableDictionary<string, string> Options(string folder)
    {
        var path = Path.Combine(Root, folder, ".globalconfig");
        if (!File.Exists(path))
        {
#pragma warning disable IDE0301 // An empty collection expression does not compile for ImmutableDictionary on net8.0.
            return ImmutableDictionary<string, string>.Empty;
#pragma warning restore IDE0301
        }

        var options = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith('['))
            {
                continue;
            }

            options[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        return options.ToImmutable();
    }
}
