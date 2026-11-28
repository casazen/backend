using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casazen.Tests.Unit.Services;

internal static class CrmTestWorld
{
    public static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    public static DateTime Day(int offsetDays) => new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc).AddDays(offsetDays);

    public static FakeTimeProvider Clock() => new(Now);

    public static DbContextOptions<AppDbContext> NewStore() =>
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    public static AppDbContext Open(DbContextOptions<AppDbContext> store) => new(store);

    public static Guest AddGuest(
        AppDbContext db,
        Guid orgId,
        string firstName,
        string lastName,
        string email,
        string phone = "",
        DateTime? dateOfBirth = null)
    {
        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            PhoneNumber = phone,
            Country = "Italia",
            DateOfBirth = dateOfBirth,
            CreatedAt = Day(-60),
            UpdatedAt = Day(-60),
        };
        db.Guests.Add(guest);
        return guest;
    }

    public static Booking AddBooking(
        AppDbContext db,
        Guest guest,
        Guid propertyId,
        BookingStatus status,
        BookingSource source,
        int checkInOffset,
        int nights,
        decimal total)
    {
        var booking = new Booking
        {
            OrgId = guest.OrgId,
            PropertyId = propertyId,
            GuestId = guest.Id,
            Status = status,
            Source = source,
            CheckInDate = Day(checkInOffset),
            CheckOutDate = Day(checkInOffset + nights),
            NumberOfGuests = 2,
            BasePrice = total,
            TotalPrice = total,
            CreatedAt = Day(checkInOffset - 30),
            UpdatedAt = Day(checkInOffset - 30),
        };
        db.Bookings.Add(booking);
        return booking;
    }

    public static GuestMatchingService Matching(AppDbContext db, TimeProvider clock, CrmOptions? options = null) =>
        new(db, CrmTestIndex.Create(), Options.Create(options ?? new CrmOptions()), clock, NullLogger<GuestMatchingService>.Instance);

    public static CrmContactService Contacts(AppDbContext db, TimeProvider clock, CrmOptions? options = null)
    {
        var index = CrmTestIndex.Create();
        return new CrmContactService(
            db,
            index,
            new SuppressionService(db, index, NullLogger<SuppressionService>.Instance),
            Options.Create(options ?? new CrmOptions()),
            clock,
            NullLogger<CrmContactService>.Instance);
    }

    public static TestFeatureFlags ModuleOn() => new(FeatureFlags.CrmMarketing);

    /// <summary>The CRM sync, with the marketing module on unless <paramref name="flags"/> says otherwise.</summary>
    public static CrmProfileSyncService Sync(AppDbContext db, TimeProvider clock, CrmOptions? options = null, IFeatureFlags? flags = null)
    {
        var index = CrmTestIndex.Create();
        return new CrmProfileSyncService(
            db,
            Matching(db, clock, options),
            Contacts(db, clock, options),
            index,
            flags ?? ModuleOn(),
            clock,
            NullLogger<CrmProfileSyncService>.Instance);
    }
}
