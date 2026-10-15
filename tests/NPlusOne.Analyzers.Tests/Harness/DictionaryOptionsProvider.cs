using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NPlusOne.Analyzers.Tests.Harness;

/// <summary>Analyzer options from a dictionary, the same for every file, as a <c>.globalconfig</c> gives them.</summary>
public sealed class DictionaryOptionsProvider(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptionsProvider
{
    private readonly DictionaryOptions _options = new(values);

    /// <inheritdoc />
    public override AnalyzerConfigOptions GlobalOptions => _options;

    /// <inheritdoc />
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;

    /// <inheritdoc />
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;

    private sealed class DictionaryOptions(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
    }
}
