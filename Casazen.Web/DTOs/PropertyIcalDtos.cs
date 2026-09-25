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

public class CalendarItemDto
{
    public string Type { get; set; } = "booking";
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime StartDateUtc { get; set; }
    public DateTime EndDateUtc { get; set; }
    public string? Status { get; set; }
    public string? Source { get; set; }
    public int? NumberOfGuests { get; set; }
    public decimal? TotalPrice { get; set; }
    public string? GuestName { get; set; }
    public string? Summary { get; set; }

    /// <summary>Label of the feed (a block) or of the feed of an OTA stay created from iCal (a booking), CO-21.</summary>
    public string? ChannelLabel { get; set; }

    /// <summary>Block only: <c>ICalImport</c> or <c>Manual</c>.</summary>
    public string? BlockSource { get; set; }

    /// <summary>Block only: its import feed and the channel of that feed (<c>Airbnb</c>, <c>BookingCom</c>, <c>Other</c>).</summary>
    public Guid? FeedId { get; set; }

    public string? Channel { get; set; }

    /// <summary>Block only: the OTA stay created from it, while not cancelled (CO-21).</summary>
    public Guid? BookingId { get; set; }

    /// <summary>Block only: the host may turn it into an OTA stay ("Crea soggiorno OTA").</summary>
    public bool? Convertible { get; set; }

    /// <summary>Booking only: the import feed of an OTA stay created from iCal, and why it is "da verificare".</summary>
    public Guid? IcalFeedId { get; set; }

    public string? OtaReviewReason { get; set; }
}
