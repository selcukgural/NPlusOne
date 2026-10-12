using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace NPlusOne.Analyzers.Tests.Harness;

/// <summary>
/// Compares the analyzer's diagnostics with the <c>// expect: NP0001</c> markers in the sources:
/// a marked line must get exactly the listed ids, an unmarked line none.
/// </summary>
public static partial class ExpectedDiagnostics
{
    /// <summary>One diagnostic id on one line of one file.</summary>
    public sealed record Mark(string Path, int Line, string Id)
    {
        public override string ToString() => $"{System.IO.Path.GetFileName(Path)}({Line}): {Id}";
    }

    /// <summary>
    /// The markers in the given sources: comments in code the compiler sees, so a marker inside an inactive
    /// <c>#if</c> (a case for a newer framework) does not count.
    /// </summary>
    public static ImmutableHashSet<Mark> Parse(IEnumerable<TestCompilation.Source> sources)
    {
        var marks = ImmutableHashSet.CreateBuilder<Mark>();
        foreach (var source in sources)
        {
            var tree = CSharpSyntaxTree.ParseText(source.Text, TestCompilation.ParseOptions, source.Path);
            foreach (var comment in tree.GetRoot().DescendantTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)))
            {
                var match = Marker().Match(comment.ToString());
                if (!match.Success)
                {
                    continue;
                }

                var line = comment.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                foreach (var id in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    marks.Add(new Mark(source.Path, line, id));
                }
            }
        }

        return marks.ToImmutable();
    }

    /// <summary>The diagnostics as marks: the line each one starts on.</summary>
    public static ImmutableHashSet<Mark> FromDiagnostics(IEnumerable<Diagnostic> diagnostics)
        => [.. diagnostics.Select(d => new Mark(d.Location.SourceTree!.FilePath, d.Location.GetLineSpan().StartLinePosition.Line + 1, d.Id))];

    /// <summary>Fails with every missed and every unexpected diagnostic, and each unexpected one's message.</summary>
    public static void Verify(IReadOnlyList<TestCompilation.Source> sources, ImmutableArray<Diagnostic> diagnostics)
    {
        var expected = Parse(sources);
        var actual = FromDiagnostics(diagnostics);
        var missed = expected.Except(actual).OrderBy(m => m.Path).ThenBy(m => m.Line).ToList();
        var unexpected = diagnostics
            .Where(d => !expected.Contains(FromDiagnostics([d]).Single()))
            .OrderBy(d => d.Location.SourceTree!.FilePath).ThenBy(d => d.Location.SourceSpan.Start)
            .ToList();

        if (missed.Count == 0 && unexpected.Count == 0)
        {
            return;
        }

        var message = new System.Text.StringBuilder();
        message.AppendLine(CultureInfo.InvariantCulture, $"{missed.Count} missed, {unexpected.Count} unexpected (of {expected.Count} expected).");
        foreach (var m in missed)
        {
            message.AppendLine(CultureInfo.InvariantCulture, $"  missed:     {m}");
        }

        foreach (var d in unexpected)
        {
            message.AppendLine(CultureInfo.InvariantCulture, $"  unexpected: {FromDiagnostics([d]).Single()} {d.GetMessage(CultureInfo.InvariantCulture)}");
        }

        Assert.Fail(message.ToString());
    }

    [GeneratedRegex(@"//\s*expect:\s*([A-Z0-9, ]+)\s*$")]
    private static partial Regex Marker();
}
