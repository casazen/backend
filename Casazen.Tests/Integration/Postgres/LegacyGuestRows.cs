using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// Guest rows for migration tests that seed a schema from <c>GuestPrivacyConsentsAndRetention</c> on (CO-15 dropped the
/// required <c>DataRetentionUntil</c>; before that point a test writes its own guest row, as
/// <c>FieldEncryptionPostgresTests</c> does): the columns every <c>Guests</c> row needs, from the entity. The others are
/// nullable or have a database default. Same reasoning as <see cref="LegacyPropertyRows"/>: a column that a later migration
/// adds to <see cref="Guest"/> cannot break the test; set it with an <c>UPDATE</c> after the migration that adds it. The
/// document number is stored as it is (no data protection provider: the encryption of CO-14 only applies when the context
/// has one).
/// </summary>
internal static class LegacyGuestRows
{
    public static Task InsertAsync(AppDbContext db, Guest guest) =>
        db.Database.ExecuteSqlInterpolatedAsync(Statement(guest));

    internal static FormattableString Statement(Guest guest) => $"""
        INSERT INTO "Guests" (
            "Id", "OrgId", "FirstName", "LastName", "Email", "PhoneNumber", "Address", "City", "PostalCode", "Country",
            "PlaceOfBirth", "Nationality", "DocumentNumber", "DocumentIssuingCountry", "ConsentIpAddress", "ErasureRequested",
            "Notes", "ConsentVersion", "MarketingConsent", "DataProcessingPurpose", "IsDeleted", "DeletionReason",
            "CreatedAt", "UpdatedAt")
        VALUES ({guest.Id}, {guest.OrgId}, {guest.FirstName}, {guest.LastName}, {guest.Email}, {guest.PhoneNumber},
            {guest.Address}, {guest.City}, {guest.PostalCode}, {guest.Country}, {guest.PlaceOfBirth}, {guest.Nationality},
            {guest.DocumentNumber}, {guest.DocumentIssuingCountry}, {guest.ConsentIpAddress}, {guest.ErasureRequested},
            {guest.Notes}, {guest.ConsentVersion}, {guest.MarketingConsent}, {guest.DataProcessingPurpose}, {guest.IsDeleted},
            {guest.DeletionReason}, {guest.CreatedAt}, {guest.UpdatedAt});
        """;
}
