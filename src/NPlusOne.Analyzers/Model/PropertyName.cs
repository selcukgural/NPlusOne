using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Model;

/// <summary>A property definition by name, valid across compilations with the very same referenced assemblies (see <see cref="TypeName"/>).</summary>
/// <param name="Type">The type declaring it.</param>
/// <param name="Name">Its name.</param>
internal sealed record PropertyName(TypeName Type, string Name)
{
    /// <summary>The names of properties, or null when one cannot be named.</summary>
    /// <param name="compilation">The compilation the properties are seen from.</param>
    /// <param name="properties">The properties.</param>
    public static PropertyName[]? Of(Compilation compilation, IReadOnlyList<IPropertySymbol> properties)
    {
        var names = new PropertyName[properties.Count];
        for (var i = 0; i < properties.Count; i++)
        {
            if (TypeName.Of(compilation, properties[i].ContainingType) is not { } type)
            {
                return null;
            }

            names[i] = new PropertyName(type, properties[i].Name);
        }

        return names;
    }

    /// <summary>The property definitions of names, or null when one is not found.</summary>
    /// <param name="compilation">A compilation with the same referenced assemblies as the one the names were taken from.</param>
    /// <param name="names">The names.</param>
    public static List<IPropertySymbol>? Resolve(Compilation compilation, PropertyName[] names)
    {
        var properties = new List<IPropertySymbol>(names.Length);
        foreach (var name in names)
        {
            if (name.Type.Resolve(compilation)?.GetMembers(name.Name).OfType<IPropertySymbol>().FirstOrDefault() is not { } property)
            {
                return null;
            }

            properties.Add(property);
        }

        return properties;
    }
}
