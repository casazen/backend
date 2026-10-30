using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs;

/// <summary>New import feed of a property (PC-11).</summary>
public class PropertyIcalFeedCreateRequest
{
    /// <summary>Airbnb, BookingCom or Other; when missing it is taken from the host of the URL.</summary>
    public ICalFeedChannel? Channel { get; set; }

    /// <summary>Optional label shown instead of the channel (max 60 characters, no line breaks).</summary>
    [MaxLength(PropertyICalFeed.LabelMaxLength, ErrorMessage = "ICalFeedInvalidLabel")]
    public string? Label { get; set; }

    /// <summary>The https iCal export link of the OTA. Stored encrypted, never returned.</summary>
    [MaxLength(PropertyICalFeed.ImportUrlMaxLength, ErrorMessage = "ICalInvalidUrl")]
    public string ImportUrl { get; set; } = string.Empty;
}

/// <summary>
/// State of one import feed. Same error contract as FD-16/PC-10, per feed: <see cref="LastImportStatus"/>,
/// <see cref="LastErrorCode"/> and its localized <see cref="LastError"/>. The URL is only returned masked.
/// </summary>
public class PropertyIcalFeedDto
{
    public Guid Id { get; set; }

    /// <summary>Airbnb, BookingCom or Other.</summary>
    public string Channel { get; set; } = nameof(ICalFeedChannel.Other);

    public string? Label { get; set; }

    /// <summary>Host and last characters of the import URL (e.g. <c>www.airbnb.it/…9f3a</c>), never the URL.</summary>
    public string? MaskedImportUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastImportAt { get; set; }

    /// <summary>Success, Failure, or Syncing while a sync of the feed is queued.</summary>
    public string? LastImportStatus { get; set; }

    /// <summary>Stable code of the last sync error (<c>ical_unreachable</c>, <c>ical_too_large</c>, ...).</summary>
    public string? LastErrorCode { get; set; }

    /// <summary>Localized message of <see cref="LastErrorCode"/>, never an exception message.</summary>
    public string? LastError { get; set; }

    /// <summary>Blocks imported by this feed.</summary>
    public int BlockCount { get; set; }
}

/// <summary>iCal state of a property: export link, all blocks, and the import feeds (PC-11).</summary>
public class PropertyIcalStatusDto
{
    public string ExportUrl { get; set; } = string.Empty;

    /// <summary>Every calendar block of the property (all feeds and manual blocks).</summary>
    public int BlockCount { get; set; }

    public IReadOnlyList<PropertyIcalFeedDto> Feeds { get; set; } = [];
}

public class PropertyIcalExportUrlDto
{
    public string ExportUrl { get; set; } = string.Empty;
}

/// <summary>
/// The calendars of all the host's properties in one read (SR-10b, <c>GET /api/properties/calendar-overview</c>): per active
/// short-rent property of the caller's reach, its export link and its import feeds, with the two numbers the page may say about the
/// sync so that it promises no frequency of its own.
/// </summary>
public sealed class HostCalendarOverviewDto
{
    /// <summary>Minutes between two rounds of the job that syncs every feed (<c>property-ical-sync</c>).</summary>
    public int SyncEveryMinutes { get; init; }

    /// <summary>A feed whose last try is older than this many minutes has not been synced for a while: the page marks it "to check".</summary>
    public int StaleAfterMinutes { get; init; }

    /// <summary>By name; a property with no feed is there too, with an empty <see cref="HostCalendarOverviewPropertyDto.Feeds"/>.</summary>
    public IReadOnlyList<HostCalendarOverviewPropertyDto> Properties { get; init; } = [];
}

public sealed class HostCalendarOverviewPropertyDto
{
    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    /// <summary>
    /// The link the host pastes on the portals, or null while nobody asked for it (<c>GET /api/properties/{id}/ical/export-url</c>
    /// creates it). Whoever has it sees the dates taken: the answer is <c>private, no-store</c> and the link is never logged.
    /// </summary>
    public string? ExportUrl { get; init; }

    public IReadOnlyList<HostCalendarOverviewFeedDto> Feeds { get; init; } = [];
}

/// <summary>State of one import feed in the overview: the dashboard's contract (stable error code and localized message), never the URL.</summary>
public sealed class HostCalendarOverviewFeedDto
{
    public Guid FeedId { get; init; }

    /// <summary>Airbnb, BookingCom or Other.</summary>
    public string Channel { get; init; } = string.Empty;

    public string? Label { get; init; }

    public DateTime? LastImportAt { get; init; }

    /// <summary>Success, PartialFailure, Failure, Syncing; null when never synced.</summary>
    public string? LastImportStatus { get; init; }

    /// <summary>Stable code of the last sync error (<c>ical_unreachable</c>, ...).</summary>
    public string? LastErrorCode { get; init; }

    /// <summary>Localized message of <see cref="LastErrorCode"/>, never an exception message.</summary>
    public string? LastError { get; init; }

    /// <summary>A sync of the feed is queued or running (<c>LastImportStatus</c> is <c>Syncing</c>).</summary>
    public bool Syncing { get; init; }
}

/// <summary>
/// One entry of <c>GET /api/bookings/calendar</c>: a booking (<see cref="Type"/> <c>booking</c>, opens the booking
/// detail) or a calendar block (<c>ical-block</c>: dates taken on another channel, it has no guest and no booking detail).
/// </summary>
public class CalendarItemDto
{
    public string Type { get; set; } = "booking";
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }

    /// <summary>Arrival day (stay date, no time zone: <c>2026-09-30T00:00:00</c>), MO-06.</summary>
    public DateTime StartDate { get; set; }

    /// <summary>Departure day (stay date, no time zone), MO-06.</summary>
    public DateTime EndDate { get; set; }
    public DateTime StartDateUtc { get; set; }
    public DateTime EndDateUtc { get; set; }
    public string? Status { get; set; }
    public string? Source { get; set; }
    public int? NumberOfGuests { get; set; }
    public decimal? TotalPrice { get; set; }
    public string? GuestName { get; set; }
    public string? Summary { get; set; }

    /// <summary>
    /// <c>ical-block</c> only: channel of the feed the block was imported from (<c>Airbnb</c>, <c>BookingCom</c>,
    /// <c>Other</c>, as <c>ICalFeedChannel</c>); null for a block that does not come from a feed (MO-06).
    /// </summary>
    public string? Channel { get; set; }

    /// <summary><c>ical-block</c> only: label the host gave to the feed (e.g. "Booking.com - camera 2"), or null.</summary>
    public string? FeedLabel { get; set; }

    /// <summary><c>ical-block</c> only: <c>ICalImport</c> or <c>Manual</c> (CO-21).</summary>
    public string? BlockSource { get; set; }

    /// <summary>
    /// <c>ical-block</c> with <see cref="BlockSource"/> <c>Manual</c> only: why the host closed the dates (<c>Owner</c>,
    /// <c>Maintenance</c>, <c>Other</c>, PC-09); <see cref="Summary"/> is then the host's note.
    /// </summary>
    public string? BlockReason { get; set; }

    /// <summary><c>ical-block</c> only: its import feed (CO-21).</summary>
    public Guid? FeedId { get; set; }

    /// <summary><c>ical-block</c> only: the OTA stay created from it, while not cancelled (CO-21).</summary>
    public Guid? BookingId { get; set; }

    /// <summary><c>ical-block</c> only: the host may turn it into an OTA stay ("Crea soggiorno OTA", CO-21).</summary>
    public bool? Convertible { get; set; }

    /// <summary><c>booking</c> only: the import feed of an OTA stay created from iCal (CO-21).</summary>
    public Guid? IcalFeedId { get; set; }

    /// <summary><c>booking</c> only: label of that feed when the stay was created (CO-21).</summary>
    public string? ChannelLabel { get; set; }

    /// <summary><c>booking</c> only: why the OTA stay is "da verificare" (<c>BlockRemoved</c>, <c>BlockDatesChanged</c>).</summary>
    public string? OtaReviewReason { get; set; }
}
