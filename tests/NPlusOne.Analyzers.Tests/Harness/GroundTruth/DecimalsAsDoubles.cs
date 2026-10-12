using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NPlusOne.Analyzers.Tests.Harness.GroundTruth;

/// <summary>
/// Stores <c>decimal</c> properties as SQLite reals. EF Core 8's SQLite provider cannot <c>Sum</c> decimals
/// in SQL; a corpus case must not fail for a provider limit that SQL Server does not have.
/// </summary>
public sealed class DecimalsAsDoubles(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (property.ClrType == typeof(decimal))
            {
                property.SetValueConverter(new ValueConverter<decimal, double>(v => (double)v, v => (decimal)v));
            }
            else if (property.ClrType == typeof(decimal?))
            {
                property.SetValueConverter(new ValueConverter<decimal?, double?>(v => (double?)v, v => (decimal?)v));
            }
        }
    }
}
