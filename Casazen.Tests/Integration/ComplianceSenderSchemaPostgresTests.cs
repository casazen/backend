using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The two data rules of the schema of E03/F01/US05/T01, on a real PostgreSQL database, where the constraints live: a property has one
/// sender and its ISTAT sender is its Alloggiati sender, so two automatic senders can never be stored (ADR-029-R1, ADR-029-R5); a booking
/// has at most one open attempt of the Alloggiati transmission, so two callers can never both transmit it (AW-R04, ADR-015-R6).
/// </summary>
public class ComplianceSenderSchemaPostgresTests
{
    private const string Admin = "auth0|sender-admin";

    private static readonly Guid AccountKey = Guid.Parse("5b0c9e2a-7d41-4f3a-9e8b-1c2d3e4f5a61");

    [PostgresFact]
    public async Task PropertyComplianceSender_IstatMustFollowAlloggiati_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("sender");
        var seed = await SeedAsync(database);

        // Two different senders for one property: the check constraint refuses the row.
        await using (var db = database.CreateContext())
        {
            db.PropertyComplianceSenders.Add(Sender(seed, ComplianceSenderType.ChannelManager, ComplianceSenderType.CasaZen));
            var mixed = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(mixed.InnerException);
            Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
            Assert.Equal(PropertyComplianceSender.IstatFollowsAlloggiatiConstraintName, postgres.ConstraintName);
        }

        // The same sender for both: stored.
        await using (var db = database.CreateContext())
        {
            db.PropertyComplianceSenders.Add(Sender(seed, ComplianceSenderType.CasaZen, ComplianceSenderType.CasaZen));
            await db.SaveChangesAsync();
        }

        // A second row for the same property, even a coherent one: the unique index refuses it.
        await using (var db = database.CreateContext())
        {
            db.PropertyComplianceSenders.Add(Sender(seed, ComplianceSenderType.Manual, ComplianceSenderType.Manual));
            var second = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(second.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal(PropertyComplianceSender.PropertyIndexName, postgres.ConstraintName);
        }

        // An update that would split the pair of the stored row is refused as well.
        await using (var db = database.CreateContext())
        {
            var stored = await db.PropertyComplianceSenders.SingleAsync(s => s.PropertyId == seed.PropertyId);
            stored.IstatSender = ComplianceSenderType.ChannelManager;
            var split = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(split.InnerException).SqlState);
        }

        await using (var db = database.CreateContext())
        {
            var only = await db.PropertyComplianceSenders.AsNoTracking().SingleAsync(s => s.PropertyId == seed.PropertyId);
            Assert.Equal((ComplianceSenderType.CasaZen, ComplianceSenderType.CasaZen), (only.AlloggiatiSender, only.IstatSender));
        }
    }

    [PostgresFact]
    public async Task AlloggiatiSendAttempt_OneOpenAttemptPerBooking_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("attempt");
        var seed = await SeedAsync(database);

        // The first open attempt of the booking is reserved.
        var first = Attempt(seed, AlloggiatiAttemptStatus.Reserved);
        await using (var db = database.CreateContext())
        {
            db.AlloggiatiSendAttempts.Add(first);
            await db.SaveChangesAsync();
        }

        // While it is open, a second open attempt is refused, whatever its open status: no double transmission.
        foreach (var open in new[] { AlloggiatiAttemptStatus.Reserved, AlloggiatiAttemptStatus.Sending, AlloggiatiAttemptStatus.ToVerify })
        {
            await using var db = database.CreateContext();
            db.AlloggiatiSendAttempts.Add(Attempt(seed, open));
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(refused.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal(AlloggiatiSendAttempt.OpenPerBookingIndexName, postgres.ConstraintName);
        }

        // A closed attempt next to the open one is allowed: the audit keeps every attempt.
        await using (var db = database.CreateContext())
        {
            db.AlloggiatiSendAttempts.Add(Attempt(seed, AlloggiatiAttemptStatus.TestRejected));
            await db.SaveChangesAsync();
        }

        // Once the first is closed, the next attempt of the booking can be reserved.
        await using (var db = database.CreateContext())
        {
            var stored = await db.AlloggiatiSendAttempts.SingleAsync(a => a.Id == first.Id);
            stored.Status = AlloggiatiAttemptStatus.Accepted;
            stored.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            db.AlloggiatiSendAttempts.Add(Attempt(seed, AlloggiatiAttemptStatus.Reserved));
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateContext())
        {
            var statuses = await db.AlloggiatiSendAttempts.AsNoTracking()
                .Where(a => a.BookingId == seed.BookingId)
                .Select(a => a.Status)
                .ToListAsync();
            Assert.Equal(
                new[] { AlloggiatiAttemptStatus.Reserved, AlloggiatiAttemptStatus.TestRejected, AlloggiatiAttemptStatus.Accepted }.Order(),
                statuses.Order());
        }
    }

    private sealed record Seed(Guid OrgId, Guid PropertyId, Guid BookingId);

    /// <summary>An org with one property and one booking of it: the rows the sender and the attempts belong to.</summary>
    private static async Task<Seed> SeedAsync(PostgresTestDatabase database)
    {
        await using var db = database.CreateContext();
        var org = new OrgEntity
        {
            Name = "Org TEST sender",
            Slug = $"sender-{Guid.NewGuid():N}",
            DisplayName = "Org TEST sender",
            ContactEmail = "sender@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        var property = new Property
        {
            OwnerId = Admin,
            OrgId = org.Id,
            Name = "Casa TEST",
            Description = "Casa",
            Address = "Via Roma 1",
            City = "Firenze",
            PostalCode = "50100",
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
        };
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Giulia",
            LastName = "Bianchi",
            Email = $"giulia.{Guid.NewGuid():N}@example.com",
        };
        var checkIn = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 1,
            NumberOfAdults = 1,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            BasePrice = 300m,
            TotalPrice = 300m,
            FreeRefundDeadline = checkIn.AddDays(-7),
        };
        db.AddRange(org, property, guest, booking);
        await db.SaveChangesAsync();
        return new Seed(org.Id, property.Id, booking.Id);
    }

    private static PropertyComplianceSender Sender(Seed seed, ComplianceSenderType alloggiati, ComplianceSenderType istat) => new()
    {
        OrgId = seed.OrgId,
        PropertyId = seed.PropertyId,
        AlloggiatiSender = alloggiati,
        IstatSender = istat,
        LastChangedAt = DateTime.UtcNow,
        LastChangedByUserId = Admin,
    };

    private static AlloggiatiSendAttempt Attempt(Seed seed, AlloggiatiAttemptStatus status)
    {
        var now = DateTime.UtcNow;
        return new AlloggiatiSendAttempt
        {
            OrgId = seed.OrgId,
            BookingId = seed.BookingId,
            PropertyId = seed.PropertyId,
            AccountKey = AccountKey,
            CredentialVersion = 1,
            Mode = AlloggiatiMode.Simulated,
            Status = status,
            Trigger = AlloggiatiAttemptTriggers.Host,
            SnapshotHash = new string('a', AlloggiatiSendAttempt.SnapshotHashLength),
            GuestCount = 1,
            LeaseExpiresAt = now.AddMinutes(5),
            ReservedAt = now,
        };
    }
}
