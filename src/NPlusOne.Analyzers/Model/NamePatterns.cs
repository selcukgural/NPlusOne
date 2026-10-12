using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Model;

/// <summary>
/// A list of name patterns such as <c>MyApp.Data.IOrderRepository.*</c> or <c>*Repository</c>: <c>*</c>
/// matches any run of characters, a pattern with a dot matches a full name, one without a dot the
/// simple name.
/// </summary>
/// <param name="Patterns">The patterns, already split and trimmed.</param>
internal sealed record NamePatterns(ImmutableArray<string> Patterns)
{
    /// <summary>Whether no pattern is set.</summary>
    public bool IsEmpty => Patterns.IsDefaultOrEmpty;

    /// <summary>Parses a <c>;</c>- or <c>,</c>-separated list.</summary>
    /// <param name="value">The option value, or null when it is not set.</param>
    public static NamePatterns Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new NamePatterns(ImmutableArray<string>.Empty);
        }

        var parts = value!.Split([';', ','], StringSplitOptions.RemoveEmptyEntries);
        var builder = ImmutableArray.CreateBuilder<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                builder.Add(trimmed);
            }
        }

        return new NamePatterns(builder.ToImmutable());
    }

    /// <summary>
    /// Whether a method matches, by its own name or by the name of an interface member it
    /// implements: <c>Namespace.Type.Method</c> for patterns with a dot, the method name otherwise.
    /// </summary>
    /// <param name="method">The method being called.</param>
    public bool MatchesMethod(IMethodSymbol method)
    {
        if (IsEmpty)
        {
            return false;
        }

        if (MatchesOneMethod(method))
        {
            return true;
        }

        foreach (var implemented in Symbols.ImplementedInterfaceMembers(method))
        {
            if (MatchesOneMethod(implemented))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a type matches, by its full name (patterns with a dot) or its simple name.</summary>
    /// <param name="type">The type to test.</param>
    public bool MatchesType(ITypeSymbol type)
    {
        if (IsEmpty)
        {
            return false;
        }

        var full = Symbols.FullName(type);
        foreach (var pattern in Patterns)
        {
            if (Wildcard.IsMatch(pattern, pattern.IndexOf('.') >= 0 ? full : type.Name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Matches one method's own name against the patterns.</summary>
    /// <param name="method">The method.</param>
    private bool MatchesOneMethod(IMethodSymbol method)
    {
        var full = Symbols.FullName(method.ContainingType) + "." + method.Name;
        foreach (var pattern in Patterns)
        {
            if (Wildcard.IsMatch(pattern, pattern.IndexOf('.') >= 0 ? full : method.Name))
            {
                return true;
            }
        }

        return false;
    }
}
