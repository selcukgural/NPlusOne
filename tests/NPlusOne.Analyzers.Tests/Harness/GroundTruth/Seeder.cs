using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NPlusOne.Analyzers.Tests.Harness.GroundTruth;

/// <summary>
/// Fills any model from its metadata alone: <c>count²</c> rows per entity type, row <c>i</c> with key <c>i</c>.
/// The first foreign key of row <c>i</c> points at principal <c>(i - 1) % count + 1</c>, the second at
/// <c>(i - 1) / count % count + 1</c>: every customer has <c>count</c> orders, every order <c>count</c> lines, and
/// the lines of one order point at <c>count</c> different products, so both a loop over many elements and a
/// single entity's collection grow with the count. A second foreign key to the same principal type
/// (<c>Customer</c>, <c>Assignee</c>) points <c>count</c> rows further, so the two never share an entity that
/// EF Core would fix up for free. A corpus case that runs one query per element runs more queries for a
/// larger count; one that does not, the same number.
/// </summary>
public static class Seeder
{

    public static void Seed(DbContext context, int count)
    {
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.IsOwned() || entityType.ClrType.IsAbstract || entityType.BaseType is not null && SharesKeysWithSibling(entityType))
            {
                continue;
            }

            var targets = ForeignKeyTargets(entityType);
            for (var i = 1; i <= count * count; i++)
            {
                var entry = entityType.HasSharedClrType
                    ? context.Set<Dictionary<string, object>>(entityType.Name).Add([])
                    : context.Add(Activator.CreateInstance(entityType.ClrType, nonPublic: true)!);
                foreach (var property in entityType.GetProperties())
                {
                    // EF Core writes the discriminator of a type in a hierarchy itself.
                    if (property.ValueGenerated != ValueGenerated.Never && !property.IsKey() && !property.IsForeignKey()
                        || property == entityType.FindDiscriminatorProperty())
                    {
                        continue;
                    }

                    // A nullable reference to the same type builds a two-level tree: the first rows are roots,
                    // every other row a child of one of them, so a walk down the tree grows with the count too.
                    if (IsTreeParent(entityType, property))
                    {
                        entry.Property(property.Name).CurrentValue = i <= count ? null : ValueFor(property, (i - 1) / count);
                        continue;
                    }

                    var row = property.IsForeignKey() && (!property.IsKey() || entityType.FindPrimaryKey()!.Properties.Count > 1)
                        && targets.TryGetValue(property, out var target)
                        ? (i - 1) / Power(count, target.Axis) % count + 1 + target.Block * count
                        : i;
                    entry.Property(property.Name).CurrentValue = ValueFor(property, row);
                }
            }
        }

        context.SaveChanges();
        context.ChangeTracker.Clear();
    }

    // Per foreign key property: the axis it varies along (0 for the first foreign key, 1 for the second) and
    // the block of the principal's rows it points into (0, or 1 for a second key to the same principal type).
    private static Dictionary<IProperty, (int Axis, int Block)> ForeignKeyTargets(IEntityType entityType)
    {
        var targets = new Dictionary<IProperty, (int Axis, int Block)>();
        var axis = 0;
        var perPrincipal = new Dictionary<IEntityType, int>();
        foreach (var foreignKey in entityType.GetForeignKeys())
        {
            var principal = foreignKey.PrincipalEntityType.GetRootType();
            var block = perPrincipal.GetValueOrDefault(principal);
            perPrincipal[principal] = block + 1;
            foreach (var property in foreignKey.Properties)
            {
                targets.TryAdd(property, (Math.Min(axis, 1), Math.Min(block, 1)));
            }

            axis++;
        }

        return targets;
    }

    private static int Power(int value, int exponent) => exponent == 0 ? 1 : value;

    // Derived types of one hierarchy share the table and the keys; only the first concrete type is seeded.
    private static bool SharesKeysWithSibling(IEntityType entityType)
    {
        var first = entityType.GetRootType().GetDerivedTypesInclusive().First(t => !t.ClrType.IsAbstract);
        return first != entityType;
    }

    private static bool IsTreeParent(IEntityType entityType, IProperty property)
        => property.IsForeignKey() && property.IsNullable
           && property.GetContainingForeignKeys().Any(fk => fk.PrincipalEntityType.GetRootType() == entityType.GetRootType());

    private static object? ValueFor(IProperty property, int i)
    {
        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (type == typeof(string))
        {
            return property.IsKey() || property.IsForeignKey() ? i.ToString(CultureInfo.InvariantCulture) : property.Name + i.ToString(CultureInfo.InvariantCulture);
        }

        if (type == typeof(Guid))
        {
            return new Guid(i, 0, 0, new byte[8]);
        }

        if (type == typeof(bool))
        {
            return false;
        }

        if (type == typeof(DateTime))
        {
            return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i);
        }

        if (type == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i);
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).GetValue(0);
        }

        if (type == typeof(byte[]))
        {
            return new byte[] { (byte)i };
        }

        if (type.IsPrimitive || type == typeof(decimal))
        {
            return Convert.ChangeType(property.IsKey() || property.IsForeignKey() ? i : i * 10, type, CultureInfo.InvariantCulture);
        }

        return property.ClrType.IsValueType ? Activator.CreateInstance(property.ClrType) : null;
    }
}
