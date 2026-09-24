using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Alloggiati;

public class AlloggiatiStatusDto
{
    public Guid BookingId { get; set; }
    public AlloggiatiWebStatus Status { get; set; }

    /// <summary>Reference of the real receipt; only with <see cref="AlloggiatiWebStatus.Inviato"/>.</summary>
    public string? ConfirmationNumber { get; set; }

    /// <summary>Stable error code; only with <see cref="AlloggiatiWebStatus.Errore"/> or <see cref="AlloggiatiWebStatus.Rifiutato"/>.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Sent (receipt) or declared sent by the host; null while not sent.</summary>
    public DateTime? ReportedAt { get; set; }

    /// <summary>Legal deadline (UTC): arrival + 24 hours, or + 6 hours for a stay of at most one night.</summary>
    public DateTime DeadlineAt { get; set; }

    public bool IsShortStay { get; set; }
    public double HoursUntilDeadline { get; set; }
    public bool IsOverdue { get; set; }
    public bool DataComplete { get; set; }

    public static AlloggiatiStatusDto From(AlloggiatiStatusInfo info) => new()
    {
        BookingId = info.BookingId,
        Status = info.Status,
        ConfirmationNumber = info.ConfirmationNumber,
        ErrorCode = info.ErrorCode,
        ReportedAt = info.ReportedAt,
        DeadlineAt = info.DeadlineAt,
        IsShortStay = info.IsShortStay,
        HoursUntilDeadline = info.HoursUntilDeadline,
        IsOverdue = info.IsOverdue,
        DataComplete = info.DataComplete,
    };
}

public class AlloggiatiSummaryDto
{
    public Guid BookingId { get; set; }
    public string GuestName { get; set; } = string.Empty;
    public string PropertyName { get; set; } = string.Empty;
    public DateTime CheckInDate { get; set; }
    public AlloggiatiWebStatus Status { get; set; }
    public bool DataComplete { get; set; }
    public bool IsOverdue { get; set; }
    public double HoursUntilDeadline { get; set; }
    public DateTime DeadlineAt { get; set; }
    public bool IsShortStay { get; set; }
}

/// <summary>Body of <c>POST /api/alloggiati/{bookingId}/mark-sent-manually</c>.</summary>
public class MarkAlloggiatiSentManuallyRequest
{
    /// <summary>Date the host sent the schedina on the Questura portal (from the check-in date to today).</summary>
    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    public DateTime? SentOn { get; set; }
}

/// <summary>Per-guest data the host copies on the Alloggiati Web portal, in the order of the record.</summary>
public class AlloggiatiGuestSummaryDto
{
    public Guid BookingId { get; set; }
    public AlloggiatiWebStatus Status { get; set; }
    public DateTime ArrivalDate { get; set; }
    public int StayDays { get; set; }

    /// <summary>The portal accepts at most 30 days per schedina.</summary>
    public bool StayExceedsMaxDays { get; set; }

    /// <summary>Guests declared on the booking; only <see cref="Guests"/> are registered in CasaZen.</summary>
    public int DeclaredGuests { get; set; }

    public IReadOnlyList<AlloggiatiGuestRowDto> Guests { get; set; } = [];

    public static AlloggiatiGuestSummaryDto From(AlloggiatiGuestSummaryInfo info) => new()
    {
        BookingId = info.BookingId,
        Status = info.Status,
        ArrivalDate = info.ArrivalDate,
        StayDays = info.StayDays,
        StayExceedsMaxDays = info.StayExceedsMaxDays,
        DeclaredGuests = info.DeclaredGuests,
        Guests = info.Guests.Select(AlloggiatiGuestRowDto.From).ToList(),
    };
}

/// <summary>One guest, fields in the order of the Alloggiati Web record. Text as stored: no table code.</summary>
public class AlloggiatiGuestRowDto
{
    public Guid GuestId { get; set; }
    public AlloggiatiGuestKind Kind { get; set; }
    public DateTime ArrivalDate { get; set; }
    public int StayDays { get; set; }
    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public Gender? Gender { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string PlaceOfBirth { get; set; } = string.Empty;
    public string Citizenship { get; set; } = string.Empty;
    public GuestDocumentType? DocumentType { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string DocumentIssuePlace { get; set; } = string.Empty;
    public IReadOnlyList<string> MissingFields { get; set; } = [];

    public static AlloggiatiGuestRowDto From(AlloggiatiGuestRow row) => new()
    {
        GuestId = row.GuestId,
        Kind = row.Kind,
        ArrivalDate = row.ArrivalDate,
        StayDays = row.StayDays,
        LastName = row.LastName,
        FirstName = row.FirstName,
        Gender = row.Gender,
        DateOfBirth = row.DateOfBirth,
        PlaceOfBirth = row.PlaceOfBirth,
        Citizenship = row.Citizenship,
        DocumentType = row.DocumentType,
        DocumentNumber = row.DocumentNumber,
        DocumentIssuePlace = row.DocumentIssuePlace,
        MissingFields = row.MissingFields,
    };
}
