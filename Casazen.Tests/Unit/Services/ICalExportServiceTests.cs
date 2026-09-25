using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Content and format of the public iCal export (PC-12, A2-22): all-day events, DTEND = departure day, stable UIDs,
/// neutral SUMMARY, no echo of the imported blocks nor of the OTA stays.
/// </summary>
public class ICalExportServiceTests
{
    private const string Busy = "Occupato";
    private const string GuestName = "Mario Rossi";
    private const string GuestEmail = "mario.rossi@example.com";

    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid OrgId = Guid.NewGuid();

    private readonly ICalExportService _service = new();

    [Fact]
    public void BuildPropertyFeed_Booking_AllDayEventWithDtEndOnTheDepartureDay()
    {
        var booking = Booking(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));

        var lines = _service.BuildPropertyFeed([booking], [], Busy).Split("\r\n");

        Assert.Contains("DTSTART;VALUE=DATE:20261010", lines);
        Assert.Contains("DTEND;VALUE=DATE:20261013", lines);
        Assert.Contains($"UID:booking-{booking.Id}", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("DTSTART:", StringComparison.Ordinal) || l.StartsWith("DTEND:", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPropertyFeed_ImportedBlockWithGuestName_IsNotExportedAndTheNameNeverAppears()
    {
        var imported = Block(CalendarBlockSource.ICalImport, feedId: Guid.NewGuid(), summary: $"Airbnb ({GuestName}, {GuestEmail})");
        var booking = Booking(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc));

        var ics = _service.BuildPropertyFeed([booking], [imported], Busy);

        Assert.DoesNotContain($"block-{imported.Id}", ics);
        Assert.DoesNotContain(imported.ExternalUid!, ics);
        Assert.DoesNotContain("Mario", ics);
        Assert.DoesNotContain("@example.com", ics);
        Assert.Single(ics.Split("\r\n"), l => l == "BEGIN:VEVENT");
    }

    [Fact]
    public void BuildPropertyFeed_ManualBlockWithGuestNameInSummary_UsesTheNeutralSummary()
    {
        var manual = Block(CalendarBlockSource.Manual, feedId: null, summary: $"{GuestName} - {GuestEmail} - pulizie");

        var lines = _service.BuildPropertyFeed([], [manual], Busy).Split("\r\n");

        Assert.Contains($"UID:block-{manual.Id}", lines);
        Assert.Contains("SUMMARY:Occupato", lines);
        Assert.DoesNotContain(lines, l => l.Contains("Mario", StringComparison.Ordinal) || l.Contains('@'));
        Assert.Contains("DTSTART;VALUE=DATE:20261205", lines);
        Assert.Contains("DTEND;VALUE=DATE:20261208", lines);
    }

    [Fact]
    public void BuildPropertyFeed_BookingWithGuestAndNotes_OnlyTheLocalizedNeutralSummary()
    {
        var booking = Booking(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));
        booking.Guest = new Guest { OrgId = OrgId, FirstName = "Mario", LastName = "Rossi", Email = GuestEmail };
        booking.SpecialRequests = "Arrivo tardi, citofono Rossi";

        var lines = _service.BuildPropertyFeed([booking], [], "Booked").Split("\r\n");

        Assert.Contains("SUMMARY:Booked", lines);
        Assert.DoesNotContain(lines, l => l.Contains("Rossi", StringComparison.Ordinal) || l.Contains('@'));
    }

    [Theory]
    [InlineData(BookingSource.Airbnb)]
    [InlineData(BookingSource.BookingCom)]
    [InlineData(BookingSource.Expedia)]
    public void BuildPropertyFeed_BookingFromAnOta_IsNotSentBackToTheOtas(BookingSource source)
    {
        var ota = Booking(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), source);
        var manual = Booking(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc), BookingSource.Manual);

        var ics = _service.BuildPropertyFeed([ota, manual], [], Busy);

        Assert.DoesNotContain($"booking-{ota.Id}", ics);
        Assert.Contains($"UID:booking-{manual.Id}", ics);
    }

    [Fact]
    public void BuildPropertyFeed_CancelledBookingAndStayWithoutNight_AreLeftOut()
    {
        var cancelled = Booking(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));
        cancelled.Status = BookingStatus.Cancelled;
        var noNight = Block(CalendarBlockSource.Manual, feedId: null, summary: null);
        noNight.StartUtc = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        noNight.EndUtc = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        var ics = _service.BuildPropertyFeed([cancelled], [noNight], Busy);

        Assert.DoesNotContain("BEGIN:VEVENT", ics);
        Assert.Contains("BEGIN:VCALENDAR", ics);
    }

    [Fact]
    public void BuildPropertyFeed_SameDataTwice_SameUidsInTheSameOrder()
    {
        var late = Booking(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 3, 0, 0, 0, DateTimeKind.Utc));
        var early = Booking(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));
        var manual = Block(CalendarBlockSource.Manual, feedId: null, summary: null);

        var first = Uids(_service.BuildPropertyFeed([late, early], [manual], Busy));
        var second = Uids(_service.BuildPropertyFeed([early, late], [manual], Busy));

        Assert.Equal([$"booking-{early.Id}", $"booking-{late.Id}", $"block-{manual.Id}"], first);
        Assert.Equal(first, second);
    }

    private static List<string> Uids(string ics) =>
        ics.Split("\r\n").Where(l => l.StartsWith("UID:", StringComparison.Ordinal)).Select(l => l["UID:".Length..]).ToList();

    private static Booking Booking(DateTime checkIn, DateTime checkOut, BookingSource source = BookingSource.Direct) => new()
    {
        Id = Guid.NewGuid(),
        PropertyId = PropertyId,
        OrgId = OrgId,
        CheckInDate = checkIn,
        CheckOutDate = checkOut,
        Status = BookingStatus.Confirmed,
        Source = source,
    };

    private static CalendarBlock Block(CalendarBlockSource source, Guid? feedId, string? summary) => new()
    {
        Id = Guid.NewGuid(),
        PropertyId = PropertyId,
        OrgId = OrgId,
        Source = source,
        FeedId = feedId,
        ExternalUid = source == CalendarBlockSource.ICalImport ? $"{Guid.NewGuid():N}@airbnb.com" : null,
        StartUtc = new DateTime(2026, 12, 5, 0, 0, 0, DateTimeKind.Utc),
        EndUtc = new DateTime(2026, 12, 8, 0, 0, 0, DateTimeKind.Utc),
        Summary = summary,
    };
}
