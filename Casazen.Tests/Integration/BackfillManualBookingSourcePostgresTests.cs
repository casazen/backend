using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-01 data migration (<see cref="BackfillManualBookingSource"/>) on real PostgreSQL: the bookings entered by hosts
/// before the <c>Manual</c> source existed become <c>Manual</c>; everything that carries a trace of the public
/// checkout, and every OTA booking, keeps its source.
/// </summary>
public class BackfillManualBookingSourcePostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private AppDbContext NewContext() => _database!.CreateContext();

    [PostgresFact]
    public async Task BackfillSql_ExistingBookings_MarksOnlyHostBookingsAsManual()
    {
        await using var seed = NewContext();
        var org = new Casazen.Core.Entities.Org { Name = "Org backfill", Slug = $"org-backfill-{Guid.NewGuid():N}", DisplayName = "Org backfill" };
        var property = new Property
        {
            OwnerId = "auth0|backfill-owner",
            OrgId = org.Id,
            Name = "Casa backfill",
            Address = "Via Roma 1",
            City = "Roma",
        };
        seed.Orgs.Add(org);
        seed.Properties.Add(property);

        var hostGuest = NewGuest(org.Id, "Booking Management");
        var checkoutGuest = NewGuest(org.Id, "Direct Booking Checkout");
        seed.Guests.AddRange(hostGuest, checkoutGuest);

        var hostConfirmed = NewBooking(property, hostGuest, BookingStatus.Confirmed);
        var hostLeftPending = NewBooking(property, hostGuest, BookingStatus.Pending);
        var checkoutImmediate = NewBooking(property, checkoutGuest, BookingStatus.Confirmed, freeRefundDeadline: true);
        var checkoutDeferred = NewBooking(property, checkoutGuest, BookingStatus.Pending, freeRefundDeadline: true);
        checkoutDeferred.StripeSetupIntentId = "seti_backfill";
        var oldCheckoutByGuest = NewBooking(property, checkoutGuest, BookingStatus.Confirmed);
        var oldCheckoutByPayment = NewBooking(property, hostGuest, BookingStatus.Confirmed);
        var airbnb = NewBooking(property, hostGuest, BookingStatus.Confirmed);
        airbnb.Source = BookingSource.Airbnb;
        seed.Bookings.AddRange(
            hostConfirmed, hostLeftPending, checkoutImmediate, checkoutDeferred, oldCheckoutByGuest, oldCheckoutByPayment, airbnb);
        seed.Payments.Add(new Payment
        {
            BookingId = oldCheckoutByPayment.Id,
            OrgId = org.Id,
            Amount = 100m,
            StripePaymentIntentId = "pi_backfill",
        });
        await seed.SaveChangesAsync();

        await using (var migrate = NewContext())
            await migrate.Database.ExecuteSqlRawAsync(BackfillManualBookingSource.BackfillSql);

        await using var db = NewContext();
        var sources = await db.Bookings.AsNoTracking()
            .Where(b => b.PropertyId == property.Id)
            .ToDictionaryAsync(b => b.Id, b => (b.Source, b.Status));

        Assert.Equal((BookingSource.Manual, BookingStatus.Confirmed), sources[hostConfirmed.Id]);
        Assert.Equal((BookingSource.Manual, BookingStatus.Pending), sources[hostLeftPending.Id]);
        Assert.Equal(BookingSource.Direct, sources[checkoutImmediate.Id].Source);
        Assert.Equal(BookingSource.Direct, sources[checkoutDeferred.Id].Source);
        Assert.Equal(BookingSource.Direct, sources[oldCheckoutByGuest.Id].Source);
        Assert.Equal(BookingSource.Direct, sources[oldCheckoutByPayment.Id].Source);
        Assert.Equal(BookingSource.Airbnb, sources[airbnb.Id].Source);
    }

    private static Guest NewGuest(Guid orgId, string purpose) => new()
    {
        OrgId = orgId,
        FirstName = "Mario",
        LastName = "Rossi",
        Email = $"mario.{Guid.NewGuid():N}@example.com",
        DataProcessingPurpose = purpose,
    };

    private static Booking NewBooking(Property property, Guest guest, BookingStatus status, bool freeRefundDeadline = false)
    {
        var checkIn = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        return new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(4),
            NumberOfGuests = 2,
            Status = status,
            Source = BookingSource.Direct,
            FreeRefundDeadline = freeRefundDeadline ? checkIn.AddDays(-7) : null,
        };
    }
}
