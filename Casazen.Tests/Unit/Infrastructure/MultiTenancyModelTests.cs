using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AC1/AC2/AC6/AC9 — model shape. Asserts the EF model (the same metadata the regenerated
/// snapshot is built from) carries the Org tenant key, the unique Slug index, and a restricted
/// OrgId FK + index on every tenant-scoped table, with User.OrgId nullable.
/// </summary>
public class MultiTenancyModelTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"model-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public void Org_HasUniqueIndexOnSlug()
    {
        using var db = NewDb();
        var org = db.Model.FindEntityType(typeof(OrgEntity))!;

        var slugIndex = org.GetIndexes().SingleOrDefault(i => i.Properties.Any(p => p.Name == nameof(OrgEntity.Slug)));

        Assert.NotNull(slugIndex);
        Assert.True(slugIndex!.IsUnique);
    }

    [Theory]
    [InlineData(typeof(Property))]
    [InlineData(typeof(Booking))]
    [InlineData(typeof(LeaseContract))]
    [InlineData(typeof(Payment))]
    [InlineData(typeof(Guest))]
    [InlineData(typeof(PropertyDocument))]
    [InlineData(typeof(OtaIntegration))]
    [InlineData(typeof(PricingAdapterConfig))]
    [InlineData(typeof(PricingHistory))]
    [InlineData(typeof(SeasonalPriceSuggestion))]
    [InlineData(typeof(AlloggiatiWebReport))]
    [InlineData(typeof(GuestCheckInSession))]
    public void TenantEntity_HasRequiredRestrictedOrgIdFkAndIndex(Type clrType)
    {
        using var db = NewDb();
        var entity = db.Model.FindEntityType(clrType)!;

        var fk = entity.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(OrgEntity));
        Assert.Equal("OrgId", fk.Properties.Single().Name);
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        Assert.False(fk.Properties.Single().IsNullable);

        Assert.Contains(entity.GetIndexes(), i => i.Properties.Any(p => p.Name == "OrgId"));
    }

    [Fact]
    public void User_HasNullableRestrictedOrgIdFk()
    {
        using var db = NewDb();
        var user = db.Model.FindEntityType(typeof(User))!;

        var fk = user.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(OrgEntity));
        Assert.Equal("OrgId", fk.Properties.Single().Name);
        Assert.True(fk.Properties.Single().IsNullable);
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
    }

    [Fact]
    public void Org_IsRegisteredAsDbSet()
    {
        using var db = NewDb();
        Assert.NotNull(db.Model.FindEntityType(typeof(OrgEntity)));
    }
}
