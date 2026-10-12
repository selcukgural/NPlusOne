using Microsoft.CodeAnalysis;
using NPlusOne.Analyzers.Model;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// Which properties a JSON serializer skips without reading them. A skipped navigation never loads, so
/// serializing an entity cannot load it lazily.
/// </summary>
internal static class JsonIgnores
{
    /// <summary>
    /// Whether <paramref name="serializer"/> skips the property without calling its getter. System.Text.Json's
    /// <c>[JsonIgnore]</c> does so only with no condition or with <c>Always</c>: <c>WhenWritingNull</c> and
    /// <c>WhenWritingDefault</c> read the value to compare it, and so does .NET 10's <c>WhenWriting</c> (measured: it
    /// loads the navigation, then drops it). Newtonsoft skips on its own <c>[JsonIgnore]</c>, which has no condition,
    /// and on <c>[IgnoreDataMember]</c>, which System.Text.Json does not honour. Neither serializer honours the
    /// other's <c>[JsonIgnore]</c>.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="serializer">The serializer that walks the entity.</param>
    public static bool Skips(IPropertySymbol property, JsonSerializerKind serializer)
    {
        foreach (var attribute in property.GetAttributes())
        {
            if (attribute.AttributeClass is not { Name: "JsonIgnoreAttribute" or "IgnoreDataMemberAttribute" } type)
            {
                continue;
            }

            var name = Symbols.FullName(type);
            if (serializer == JsonSerializerKind.Newtonsoft && name is "Newtonsoft.Json.JsonIgnoreAttribute" or "System.Runtime.Serialization.IgnoreDataMemberAttribute")
            {
                return true;
            }

            if (serializer == JsonSerializerKind.SystemTextJson && name == "System.Text.Json.Serialization.JsonIgnoreAttribute")
            {
                return ConditionName(attribute) is null or "Always";
            }
        }

        return false;
    }

    /// <summary>The name of the <c>Condition</c> set on System.Text.Json's attribute, or null when none is set.</summary>
    /// <param name="attribute">The attribute.</param>
    private static string? ConditionName(AttributeData attribute)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument is not { Key: "Condition", Value: { Type: INamedTypeSymbol condition, Value: { } value } })
            {
                continue;
            }

            foreach (var member in condition.GetMembers())
            {
                if (member is IFieldSymbol { HasConstantValue: true } field && Equals(field.ConstantValue, value))
                {
                    return field.Name;
                }
            }
        }

        return null;
    }
}
