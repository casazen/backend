using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FIS-02, NF-82, NF-116 (E06/F02/US02/T01, E17/F01/US01/T02) on a real PostgreSQL database, where it counts: the issuer rows of a booking are one for each
/// document (the receipt of the owner and the invoice of the agency of one stay are two rows), and it is the database itself that refuses a second row of the
/// same document, whoever writes it.
/// </summary>
public class BookingDocumentIssuerPostgresTests
{
    /// <summary>
    /// PROOF TEST: data rule. A document has one issuer row: a second row of the same document, of the same kind or of the other one, is a unique violation
    /// of the index on the document; a row of another document of the same booking is accepted; the first row is untouched.
    /// </summary>
    [PostgresFact]
    public async Task BookingDocumentIssuer_OneRowPerDocument_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("docissuer");
        var at = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
        var orgId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var firstDocument = Guid.NewGuid();

        await using (var seed = database.CreateContext())
        {
            var propertyId = Guid.NewGuid();
            var guestId = Guid.NewGuid();
            seed.Orgs.Add(new OrgEntity
            {
                Id = orgId,
                Name = "Casa Emittente",
                Slug = $"doc-issuer-{orgId:N}"[..28],
                DisplayName = "Casa Emittente",
                ContactEmail = "doc-issuer@example.com",
                OrgType = OrgType.Host,
                PlanTier = PlanTier.Starter,
            });
            seed.Properties.Add(new Property
            {
                Id = propertyId,
                OrgId = orgId,
                OwnerId = "auth0|owner",
                Name = "Casa del Lago",
                Address = "Via del Lago 1",
                City = "Como",
            });
            seed.Guests.Add(new Guest { Id = guestId, OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"anna.{guestId:N}@example.com" });
            seed.Bookings.Add(new Booking
            {
                Id = bookingId,
                OrgId = orgId,
                PropertyId = propertyId,
                GuestId = guestId,
                CheckInDate = at.AddDays(10),
                CheckOutDate = at.AddDays(12),
                NumberOfGuests = 2,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                BasePrice = 200m,
                TotalPrice = 200m,
            });
            seed.BookingDocumentIssuers.Add(Row(orgId, bookingId, BookingDocumentIssuerKind.CasaZen, at, firstDocument));
            await seed.SaveChangesAsync();
        }

        foreach (var kind in new[] { BookingDocumentIssuerKind.CasaZen, BookingDocumentIssuerKind.Octorate })
        {
            await using var second = database.CreateContext();
            second.BookingDocumentIssuers.Add(Row(orgId, bookingId, kind, at, firstDocument));

            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(refused.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal(BookingDocumentIssuer.DocumentIndexName, postgres.ConstraintName);
        }

        // Another document of the same booking (the invoice of the agency beside the receipt of the owner) has its own row.
        var secondDocument = Guid.NewGuid();
        await using (var other = database.CreateContext())
        {
            other.BookingDocumentIssuers.Add(Row(orgId, bookingId, BookingDocumentIssuerKind.CasaZen, at, secondDocument));
            await other.SaveChangesAsync();
        }

        await using var read = database.CreateContext();
        var rows = await read.BookingDocumentIssuers.IgnoreQueryFilters().AsNoTracking().Where(i => i.BookingId == bookingId).ToListAsync();
        Assert.Equal(new[] { firstDocument, secondDocument }.Order(), rows.Select(r => r.LockedByDocumentId!.Value).Order());
        Assert.All(rows, row => Assert.Equal(BookingDocumentIssuerKind.CasaZen, row.Issuer));
    }

    private static BookingDocumentIssuer Row(Guid orgId, Guid bookingId, BookingDocumentIssuerKind kind, DateTime at, Guid documentId) =>
        new() { OrgId = orgId, BookingId = bookingId, Issuer = kind, LockedAt = at, LockedByDocumentId = documentId };
}
