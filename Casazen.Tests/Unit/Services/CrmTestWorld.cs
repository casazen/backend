using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The world of the proofs of the CRM matching, contacts and sync (E07/F01/US02): an EF InMemory database that several contexts can open (the
/// seed and the service under test do not share a change tracker, as a job and a request do not), a clock, guests and bookings of one org, and
/// the real services over the real keyed index. Today is 6 November 2026 in Rome.
/// </summary>
internal static class CrmTestWorld
{
    /// <summary>The clock of the proofs: 09:00 in Rome on 6 November 2026.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The Europe/Rome day <paramref name="offsetDays"/> from today, as the midnight UTC of its date (the storage convention of the stays).</summary>
    public static DateTime Day(int offsetDays) => new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc).AddDays(offsetDays);

    public static FakeTimeProvider Clock() => new(Now);

    /// <summary>Options of a new InMemory database; open it with <see cref="Open"/> as many times as the proof needs contexts.</summary>
    public static DbContextOptions<AppDbContext> NewStore() =>
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    /// <summary>A context on the store, with no tenant (the filter is off, as in a job): every query of the services names its org.</summary>
    public static AppDbContext Open(DbContextOptions<AppDbContext> store) => new(store);

    /// <summary>A guest of the org (added to the context, not saved).</summary>
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

    /// <summary>A booking of the guest at the property, <paramref name="checkInOffset"/> days from today, made 30 days before the arrival (added, not saved).</summary>
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
}
