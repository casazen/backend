using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// Seeding for tests that migrate a database to an earlier migration: later migrations add columns the model would write
/// (e.g. CO-18 <c>Properties.TaxpayerFiscalCode</c>), so rows are inserted with SQL, only with columns that already existed.
/// </summary>
internal static class PreviousSchemaSeed
{
    /// <summary>
    /// Inserts <paramref name="property"/> with its long-standing columns (the required ones plus <c>ComplianceStatus</c>),
    /// never with columns added by later migrations.
    /// </summary>
    public static Task InsertPropertyAsync(AppDbContext db, Property property)
    {
        var now = DateTime.UtcNow;
        var amenities = property.Amenities.Select(a => (int)a).ToArray();
        var photoUrls = property.PhotoUrls.ToArray();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Properties" ("Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "HouseRules", "Timezone", "Amenities", "PhotoUrls", "IsActive", "ComplianceStatus", "CreatedAt", "UpdatedAt")
            VALUES ({property.Id}, {property.OwnerId}, {property.OrgId}, {property.Name}, {property.Description},
                {property.Address}, {property.City}, {property.PostalCode}, {property.Latitude}, {property.Longitude},
                {property.Bedrooms}, {property.Bathrooms}, {property.MaxGuests}, {property.NightlyRate}, {property.CleaningFee},
                {property.DamageDeposit}, {property.HouseRules}, {property.Timezone}, {amenities}, {photoUrls},
                {property.IsActive}, {(int)property.ComplianceStatus}, {now}, {now})
            """);
    }
}
