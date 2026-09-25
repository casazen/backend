using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Soft delete of a property on real PostgreSQL (PC-05, A2-18): refused with <c>property_has_upcoming_bookings</c>
/// while a confirmed or checked-in stay has not checked out, otherwise the row is kept (<c>IsDeleted</c>/
/// <c>DeletedAt</c>) and excluded from every normal read; the plan's used slot is released and the fiscal reports
/// (annual income, tourist tax) still surface its historical data.
/// </summary>
public class PropertySoftDeletePostgresTests : IAsyncLifetime
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

    private static PropertyService NewPropertyService(AppDbContext db, TimeProvider clock) =>
        new(
            new PropertyRepository(db),
            Mock.Of<IPropertyComplianceStatusService>(),
            new CinDeadlineCalendar(Options.Create(new CinOptions()), clock),
            NullLogger<PropertyService>.Instance,
            clock);

    private static EntitlementService NewEntitlementService(AppDbContext db) =>
        new(db, new ConfigurationBuilder().Build());

    private static FiscalService NewFiscalService(AppDbContext db, TimeProvider clock) =>
        new(db, new MigraDocPdfDocumentRenderer(), Options.Create(new ShortStayFiscalOptions()), clock);

    [PostgresFact]
    public async Task DeletePropertyAsync_ConfirmedBookingNotCheckedOutYet_ThrowsConflictAndKeepsTheProperty()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var (orgId, propertyId) = await SeedOrgAndPropertyAsync();
        await using (var db = NewContext())
        {
            SeedBooking(db, propertyId, orgId, BookingStatus.Confirmed,
                checkIn: new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
                checkOut: new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var error = await Assert.ThrowsAsync<DomainConflictException>(
                () => NewPropertyService(db, clock).DeletePropertyAsync(propertyId));
            Assert.Equal(PropertyService.HasUpcomingBookingsCode, error.Code);
        }

        await using var check = NewContext();
        var stored = await check.Properties.SingleAsync(p => p.Id == propertyId);
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);
    }

    [PostgresFact]
    public async Task DeletePropertyAsync_GuestCheckedInAndNotYetCheckedOut_ThrowsConflict()
    {
        // An ongoing stay (guest already in the property) must block a delete even more than a future one.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero));
        var (orgId, propertyId) = await SeedOrgAndPropertyAsync();
        await using (var db = NewContext())
        {
            SeedBooking(db, propertyId, orgId, BookingStatus.CheckedIn,
                checkIn: new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
                checkOut: new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));
            await db.SaveChangesAsync();
        }

        await using var db2 = NewContext();
        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => NewPropertyService(db2, clock).DeletePropertyAsync(propertyId));
        Assert.Equal(PropertyService.HasUpcomingBookingsCode, error.Code);
    }

    [PostgresFact]
    public async Task DeletePropertyAsync_OnlyPastCheckedOutStay_SoftDeletesAndReleasesThePlanSlot()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var (orgId, propertyId) = await SeedOrgAndPropertyAsync();
        await using (var db = NewContext())
        {
            // A completed stay, already checked out before "today": must not block the delete.
            SeedBooking(db, propertyId, orgId, BookingStatus.CheckedOut,
                checkIn: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                checkOut: new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc));
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
            Assert.True((await NewEntitlementService(db).GetEntitlementAsync(orgId)).PropertyCount >= 1);

        await using (var db = NewContext())
            Assert.True(await NewPropertyService(db, clock).DeletePropertyAsync(propertyId));

        await using (var check = NewContext())
        {
            // Excluded from every normal read (the SoftDelete query filter), the row itself kept.
            Assert.Null(await check.Properties.FirstOrDefaultAsync(p => p.Id == propertyId));
            var raw = await check.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
            Assert.True(raw.IsDeleted);
            Assert.Equal(clock.GetUtcNow().UtcDateTime, raw.DeletedAt);
        }

        await using var afterDb = NewContext();
        var entitlement = await NewEntitlementService(afterDb).GetEntitlementAsync(orgId);
        Assert.Equal(0, entitlement.PropertyCount);
        Assert.True(entitlement.CanAddProperty);
    }

    [PostgresFact]
    public async Task DeletePropertyAsync_CalledAgainAfterAlreadyDeleted_IsANoOp()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var (_, propertyId) = await SeedOrgAndPropertyAsync();

        await using (var db = NewContext())
            Assert.True(await NewPropertyService(db, clock).DeletePropertyAsync(propertyId));

        clock.Advance(TimeSpan.FromDays(1));
        await using (var db = NewContext())
            Assert.True(await NewPropertyService(db, clock).DeletePropertyAsync(propertyId));

        await using var check = NewContext();
        var raw = await check.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
        // The first delete's timestamp is kept: a second delete of an already-deleted property changes nothing.
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero).UtcDateTime, raw.DeletedAt);
    }

    [PostgresFact]
    public async Task DeletePropertyAsync_ThenAnnualFiscalReportAndTouristTaxReport_StillSurfaceItsHistoricalIncome()
    {
        // PC-05 compliance requirement: soft-deleting a property must never make its fiscal history unreportable.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero));
        var (orgId, propertyId) = await SeedOrgAndPropertyAsync(city: "Firenze");
        Guid bookingId;
        await using (var db = NewContext())
        {
            var booking = SeedBooking(db, propertyId, orgId, BookingStatus.CheckedOut,
                checkIn: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                checkOut: new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc),
                totalPrice: 600m, touristTax: 30m);
            db.Payments.Add(new Payment
            {
                OrgId = orgId,
                BookingId = booking.Id,
                Amount = 600m,
                Status = PaymentStatus.Completed,
                ProcessedAt = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
            bookingId = booking.Id;
        }

        await using (var db = NewContext())
            Assert.True(await NewPropertyService(db, clock).DeletePropertyAsync(propertyId));

        await using var reportDb = NewContext();
        var fiscal = NewFiscalService(reportDb, clock);
        var scope = new HostScope(orgId, null);

        var annual = await fiscal.GetAnnualReportAsync(scope, 2026);
        var line = Assert.Single(annual.Properties, l => l.PropertyId == propertyId);
        Assert.Equal(600m, line.GrossIncome);

        var touristTax = await fiscal.GetTouristTaxReportAsync(scope, FiscalReportPeriod.WholeYear(2026));
        var stay = Assert.Single(touristTax.Stays, s => s.PropertyId == propertyId);
        Assert.Equal(30m, stay.Amount);
        Assert.Equal(bookingId, stay.BookingId);
    }

    private async Task<(Guid OrgId, Guid PropertyId)> SeedOrgAndPropertyAsync(string city = "Roma")
    {
        await using var db = NewContext();
        var org = new OrgEntity
        {
            Name = "Org PC-05",
            Slug = $"org-pc05-{Guid.NewGuid():N}",
            DisplayName = "Org PC-05",
            ContactEmail = "pc05@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        var property = new Property
        {
            OwnerId = "auth0|pc05-owner",
            OrgId = org.Id,
            Name = "Villa PC-05",
            Description = "PC-05",
            Address = $"Via PC-05 {Guid.NewGuid():N}",
            City = city,
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return (org.Id, property.Id);
    }

    private static Booking SeedBooking(
        AppDbContext db,
        Guid propertyId,
        Guid orgId,
        BookingStatus status,
        DateTime checkIn,
        DateTime checkOut,
        decimal totalPrice = 0m,
        decimal touristTax = 0m)
    {
        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = $"mario.{Guid.NewGuid():N}@example.com",
        };
        db.Guests.Add(guest);
        var booking = new Booking
        {
            PropertyId = propertyId,
            OrgId = orgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            Status = status,
            Source = BookingSource.Direct,
            BasePrice = totalPrice,
            TotalPrice = totalPrice,
            TouristTax = touristTax,
            TouristTaxAmount = touristTax,
        };
        db.Bookings.Add(booking);
        return booking;
    }
}
