using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs;

public class PropertyIcalFeedCreateRequest
{
    public ICalFeedChannel? Channel { get; set; }

    /// <summary>Optional label shown instead of the channel (max 60 characters, no line breaks).</summary>
    [MaxLength(PropertyICalFeed.LabelMaxLength, ErrorMessage = "ICalFeedInvalidLabel")]
    public string? Label { get; set; }

    [MaxLength(PropertyICalFeed.ImportUrlMaxLength, ErrorMessage = "ICalInvalidUrl")]
    public string ImportUrl { get; set; } = string.Empty;
}

public class PropertyIcalFeedDto
{
    public Guid Id { get; set; }

    public string Channel { get; set; } = nameof(ICalFeedChannel.Other);

    public string? Label { get; set; }

    /// <summary>Host and last characters of the import URL (e.g. <c>www.airbnb.it/…9f3a</c>), never the URL.</summary>
    public string? MaskedImportUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastImportAt { get; set; }

    public string? LastImportStatus { get; set; }

    public string? LastErrorCode { get; set; }

    public string? LastError { get; set; }

    public int BlockCount { get; set; }

    /// <summary>
    /// The Octorate calendar of this feed's channel replaced the iCal import (E02/F02/US01/T03, ADR-002-R11, R12, R14): the feed is no longer
    /// synced and its blocks are kept as they were, so the channels page can tell the host and advise removing CasaZen's iCal link in that
    /// channel's own settings. The iCal export of the property goes on.
    /// </summary>
    public bool Deprecated { get; set; }

    public DateTime? DeprecatedAt { get; set; }
}

public class PropertyIcalStatusDto
{
    public string ExportUrl { get; set; } = string.Empty;

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
    public int SyncEveryMinutes { get; init; }

    public int StaleAfterMinutes { get; init; }

    public IReadOnlyList<HostCalendarOverviewPropertyDto> Properties { get; init; } = [];
}

public sealed class HostCalendarOverviewPropertyDto
{
    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    public string? ExportUrl { get; init; }

    public IReadOnlyList<HostCalendarOverviewFeedDto> Feeds { get; init; } = [];
}

public sealed class HostCalendarOverviewFeedDto
{
    public Guid FeedId { get; init; }

    public string Channel { get; init; } = string.Empty;

    public string? Label { get; init; }

    public DateTime? LastImportAt { get; init; }

    public string? LastImportStatus { get; init; }

    public string? LastErrorCode { get; init; }

    public string? LastError { get; init; }

    public bool Syncing { get; init; }

    public bool Deprecated { get; init; }
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

    public string? Channel { get; set; }

    public string? FeedLabel { get; set; }

    /// <summary>
    /// <c>ical-block</c> only: <c>ICalImport</c>, <c>Manual</c> (CO-21) or <c>Octorate</c>, the nights the Octorate calendar holds closed
    /// (E02/F02/US01/T03): it has no feed and no channel, the host cannot remove it and it is never turned into a stay.
    /// </summary>
    public string? BlockSource { get; set; }

    public string? BlockReason { get; set; }

    public Guid? FeedId { get; set; }

    public Guid? BookingId { get; set; }

    public bool? Convertible { get; set; }

    public Guid? IcalFeedId { get; set; }

    public string? ChannelLabel { get; set; }

    public string? OtaReviewReason { get; set; }
}
