using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// Property rows for migration tests that migrate to an older point and seed from there. Saving a <see cref="Property"/>
/// through the model writes every column of TODAY's <c>Properties</c> table (PC-03 <c>IsPaused</c>, PC-05
/// <c>IsDeleted</c>, SU-04 <c>ComuneIstatCode</c>, PC-06 <c>Unit</c>, PM-01 <c>RentalMode</c>, and the next one…), and at an
/// older point most of them do not exist yet (<c>42703: column "RentalMode" of relation "Properties" does not exist</c>).
/// This statement names only the columns of the first <c>Properties</c> table, which every later migration keeps, so a
/// column added to <see cref="Property"/> afterwards can never break a test that uses it: every column added since is
/// nullable or has a database default. The values of the columns written come from the entity, except
/// <see cref="Property.Amenities"/> and <see cref="Property.PhotoUrls"/>, written empty. Every column of a later migration
/// (slug, CIN, unit, comune, pause, soft delete, compliance, rental mode…) takes its default: to give the property one, run
/// an <c>UPDATE</c> after the migration that adds the column. <c>LegacyRowsSchemaTests</c> proves, without a database, that
/// the statement fits the schema at the migration points where tests use it.
/// </summary>
internal static class LegacyPropertyRows
{
    public static Task InsertAsync(AppDbContext db, Property property) =>
        db.Database.ExecuteSqlInterpolatedAsync(Statement(property));

    internal static FormattableString Statement(Property property) => $"""
        INSERT INTO "Properties" (
            "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
            "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
            "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
        VALUES ({property.Id}, {property.OwnerId}, {property.OrgId}, {property.Name}, {property.Description},
            {property.Address}, {property.City}, {property.PostalCode}, {property.Latitude}, {property.Longitude},
            {property.Bedrooms}, {property.Bathrooms}, {property.MaxGuests}, {property.NightlyRate}, {property.CleaningFee},
            {property.DamageDeposit}, ARRAY[]::integer[], ARRAY[]::text[], {property.HouseRules}, {property.Timezone},
            {property.IsActive}, {property.CreatedAt}, {property.UpdatedAt});
        """;
}
