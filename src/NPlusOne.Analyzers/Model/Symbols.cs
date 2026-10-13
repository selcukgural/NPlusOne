using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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

    /// <summary>
    /// Every type in a namespace tree, nested types included. Types are asked for their nested types only: their other
    /// members would make the compiler bind every signature of every type, again in each keystroke's compilation.
    /// </summary>
    /// <param name="root">The root namespace.</param>
    public static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol root)
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is INamespaceSymbol ns)
            {
                foreach (var child in ns.GetNamespaceMembers())
                {
                    stack.Push(child);
                }
            }

            foreach (var type in current.GetTypeMembers())
            {
                yield return type;
                stack.Push(type);
            }
        }
    }

    /// <summary>
    /// Whether a type can derive from another class or implement an interface: false only when its source shows no base
    /// list (<c>class Order { ... }</c> derives from <c>object</c> and implements nothing). Asking the syntax first keeps
    /// the compiler from resolving the base types of every type in the compilation, again on each keystroke.
    /// </summary>
    /// <param name="type">The type.</param>
    public static bool MayDeriveOrImplement(INamedTypeSymbol type)
    {
        var references = type.DeclaringSyntaxReferences;
        if (references.IsEmpty)
        {
            return true;
        }

        foreach (var reference in references)
        {
            if (reference.GetSyntax() is not BaseTypeDeclarationSyntax { BaseList: null })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a type can override a virtual member: false only when its source declares no member with
    /// <c>override</c>. Asking the syntax first keeps the compiler from building the members of every type.
    /// </summary>
    /// <param name="type">The type.</param>
    public static bool MayOverride(INamedTypeSymbol type)
    {
        var references = type.DeclaringSyntaxReferences;
        if (references.IsEmpty)
        {
            return true;
        }

        foreach (var reference in references)
        {
            if (reference.GetSyntax() is not TypeDeclarationSyntax declaration)
            {
                return true;
            }

            foreach (var member in declaration.Members)
            {
                if (member.Modifiers.Any(SyntaxKind.OverrideKeyword))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a property's source declares it with a built-in type (<c>int</c>, <c>string?</c>, ...), which is never an
    /// entity or a collection of entities. Asking the syntax first spares binding the type of every scalar property.
    /// </summary>
    /// <param name="property">The property.</param>
    public static bool HasBuiltInType(IPropertySymbol property)
        => property.DeclaringSyntaxReferences is { Length: 1 } references
           && references[0].GetSyntax() is PropertyDeclarationSyntax { Type: PredefinedTypeSyntax or NullableTypeSyntax { ElementType: PredefinedTypeSyntax } };

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
