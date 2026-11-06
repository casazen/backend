using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FIS-02, NF-82 (E06/F02/US02/T01) on a real PostgreSQL database, where it counts: a booking never has two issuers of its documents, and it is
/// the database itself that refuses the second one, whoever writes it.
/// </summary>
public class BookingDocumentIssuerPostgresTests
{
    /// <summary>
    /// PROOF TEST: data rule. A booking locked to one issuer cannot get a second issuer row, of the same kind or of the other one: the unique
    /// index on the booking answers with a unique violation; the first row is untouched.
    /// </summary>
    [PostgresFact]
    public async Task BookingDocumentIssuer_OneRowPerBooking_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("docissuer");
        var at = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
        var orgId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

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
            seed.BookingDocumentIssuers.Add(
                new BookingDocumentIssuer { OrgId = orgId, BookingId = bookingId, Issuer = BookingDocumentIssuerKind.Octorate, LockedAt = at });
            await seed.SaveChangesAsync();
        }

        foreach (var kind in new[] { BookingDocumentIssuerKind.CasaZen, BookingDocumentIssuerKind.Octorate })
        {
            await using var second = database.CreateContext();
            second.BookingDocumentIssuers.Add(new BookingDocumentIssuer { OrgId = orgId, BookingId = bookingId, Issuer = kind, LockedAt = at });

            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(refused.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal(BookingDocumentIssuer.BookingIndexName, postgres.ConstraintName);
        }

        // The first issuer is still the only one.
        await using var read = database.CreateContext();
        var rows = await read.BookingDocumentIssuers.IgnoreQueryFilters().AsNoTracking().Where(i => i.BookingId == bookingId).ToListAsync();
        Assert.Equal(BookingDocumentIssuerKind.Octorate, Assert.Single(rows).Issuer);
    }
}
