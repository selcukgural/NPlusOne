using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// The values a serializer reaches through a result container built in place: a page, an envelope or an anonymous
/// object (<c>new Paged&lt;Order&gt;(orders, total)</c>, <c>new { items = orders }</c>). The serializer walks the
/// container's public properties, so what was put into them is serialized like a returned value.
/// </summary>
internal static class SerializedMembers
{
    /// <summary>
    /// The members of a container creation and the values put into them: every member of an anonymous object; the
    /// serialized properties an object initializer sets; and the constructor arguments whose parameter shares its
    /// name (case aside) with a serialized property, the convention records, primary constructors and
    /// System.Text.Json itself follow. A constructor argument with no such property is only used by the constructor
    /// (<c>Count = orders.Count</c>) and is not serialized. A local is followed to its only assignment.
    /// </summary>
    /// <param name="value">The serialized value, conversions removed.</param>
    /// <param name="serializer">The serializer; properties it ignores are not walked.</param>
    public static IEnumerable<(string Name, IOperation Value)> Of(IOperation value, JsonSerializerKind serializer)
    {
        if (value is ILocalReferenceOperation local)
        {
            value = Operations.SingleInitializer(local) is { } initializer ? Operations.StripConversions(initializer)! : value;
        }

        if (value is IAnonymousObjectCreationOperation anonymous)
        {
            foreach (var initializer in anonymous.Initializers)
            {
                if (initializer is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation target } assignment)
                {
                    yield return (target.Property.Name, assignment.Value);
                }
            }

            yield break;
        }

        if (value is not IObjectCreationOperation { Type: INamedTypeSymbol type } creation)
        {
            yield break;
        }

        foreach (var argument in creation.Arguments)
        {
            if (argument.Parameter is { } parameter
                && SerializedProperty(type, parameter.Name, serializer) is { } property)
            {
                yield return (property.Name, argument.Value);
            }
        }

        foreach (var initializer in creation.Initializer?.Initializers ?? ImmutableArray<IOperation>.Empty)
        {
            if (initializer is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation target } assignment
                && IsSerialized(target.Property, serializer))
            {
                yield return (target.Property.Name, assignment.Value);
            }
        }
    }

    /// <summary>The serialized property of a type or its base types with that name, case aside.</summary>
    /// <param name="type">The container type.</param>
    /// <param name="name">A constructor parameter's name.</param>
    /// <param name="serializer">The serializer.</param>
    private static IPropertySymbol? SerializedProperty(INamedTypeSymbol type, string name, JsonSerializerKind serializer)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IPropertySymbol property && string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && IsSerialized(property, serializer))
                {
                    return property;
                }
            }
        }

        return null;
    }

    /// <summary>Whether the serializer writes a property: public, readable, not static, not an indexer, not ignored.</summary>
    /// <param name="property">The property.</param>
    /// <param name="serializer">The serializer.</param>
    private static bool IsSerialized(IPropertySymbol property, JsonSerializerKind serializer)
        => property is { DeclaredAccessibility: Accessibility.Public, IsStatic: false, IsIndexer: false, GetMethod.DeclaredAccessibility: Accessibility.Public }
           && !JsonIgnores.Skips(property, serializer);
}
