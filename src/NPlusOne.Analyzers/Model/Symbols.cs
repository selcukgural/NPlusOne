using System.Text;
using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Model;

/// <summary>Small symbol helpers the analysis and the option patterns share.</summary>
internal static class Symbols
{
    /// <summary>
    /// The namespace-qualified name of a type without generic arguments, such as
    /// <c>MyApp.Data.IRepository</c>, which is how users write it in option patterns.
    /// </summary>
    /// <param name="type">The type.</param>
    public static string FullName(ITypeSymbol type)
    {
        var builder = new StringBuilder(type.Name);
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            builder.Insert(0, outer.Name + ".");
        }

        for (var ns = type.ContainingNamespace; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
        {
            builder.Insert(0, ns.Name + ".");
        }

        return builder.ToString();
    }

    /// <summary>A method as messages show it: <c>Type.Method</c>, or just the name of a local function.</summary>
    /// <param name="method">The method.</param>
    public static string Display(IMethodSymbol method)
    {
        if (method.MethodKind == MethodKind.LocalFunction)
        {
            return method.Name;
        }

        if (method.AssociatedSymbol is IPropertySymbol property)
        {
            return method.ContainingType.Name + "." + property.Name;
        }

        return method.ContainingType.Name + "." + method.Name;
    }

    /// <summary>The interface members a class method implements, implicitly or explicitly.</summary>
    /// <param name="method">A method declared in a class or struct.</param>
    public static IEnumerable<IMethodSymbol> ImplementedInterfaceMembers(IMethodSymbol method)
    {
        foreach (var explicitMember in method.ExplicitInterfaceImplementations)
        {
            yield return explicitMember;
        }

        var type = method.ContainingType;
        if (type is null || type.TypeKind == TypeKind.Interface)
        {
            yield break;
        }

        foreach (var iface in type.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(method.Name))
            {
                if (member is IMethodSymbol candidate
                    && SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(candidate), method))
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>Whether a call to this method can run code other than its own body: interface, abstract or virtual.</summary>
    /// <param name="method">The method being called.</param>
    public static bool IsDispatched(IMethodSymbol method)
        => method.ContainingType?.TypeKind == TypeKind.Interface || method.IsAbstract || method.IsVirtual || method.IsOverride;

    /// <summary>Whether the method's body is in the compilation being analysed.</summary>
    /// <param name="method">The method.</param>
    /// <param name="compilation">The compilation.</param>
    public static bool HasSource(IMethodSymbol method, Compilation compilation)
    {
        if (method.DeclaringSyntaxReferences.Length == 0)
        {
            return false;
        }

        var tree = method.DeclaringSyntaxReferences[0].SyntaxTree;
        return compilation.ContainsSyntaxTree(tree);
    }
}
