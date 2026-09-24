using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.CheckIn;

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

/// <summary>Per-guest data the host copies on the Alloggiati Web portal, one row per guest of the stay (CO-12).</summary>
public class AlloggiatiGuestSummaryDto
{
    public Guid BookingId { get; set; }
    public AlloggiatiWebStatus Status { get; set; }
    public DateTime ArrivalDate { get; set; }
    public int StayDays { get; set; }

    /// <summary>The portal accepts at most 30 days per schedina.</summary>
    public bool StayExceedsMaxDays { get; set; }

    /// <summary>Guests declared on the booking; <see cref="Guests"/> are the ones registered.</summary>
    public int DeclaredGuests { get; set; }

    /// <summary>Every guest has every record field and the order of the guests is valid.</summary>
    public bool DataComplete { get; set; }

    /// <summary>Data complete and every official code found: the record can be exported.</summary>
    public bool ExportReady { get; set; }

    /// <summary>Official tables not imported yet (their codes stay "to complete").</summary>
    public IReadOnlyList<AlloggiatiCodeTable> MissingCodeTables { get; set; } = [];

    /// <summary>In record order: a head of family or group before its members.</summary>
    public IReadOnlyList<AlloggiatiGuestRowDto> Guests { get; set; } = [];

    public static AlloggiatiGuestSummaryDto From(AlloggiatiGuestSummaryInfo info) => new()
    {
        BookingId = info.BookingId,
        Status = info.Status,
        ArrivalDate = info.ArrivalDate,
        StayDays = info.StayDays,
        StayExceedsMaxDays = info.StayExceedsMaxDays,
        DeclaredGuests = info.DeclaredGuests,
        DataComplete = info.DataComplete,
        ExportReady = info.ExportReady,
        MissingCodeTables = info.MissingCodeTables,
        Guests = info.Guests.Select(AlloggiatiGuestRowDto.From).ToList(),
    };
}

/// <summary>
/// One guest, fields in the order of the Alloggiati Web record. Text as stored; <see cref="Codes"/> are the official
/// codes found in the imported tables (null = to complete or not part of the line).
/// </summary>
public class AlloggiatiGuestRowDto
{
    /// <summary>Null for the booker shown before any guest is registered.</summary>
    public Guid? StayGuestId { get; set; }
    public int Position { get; set; }
    public StayGuestType Type { get; set; }
    public bool? IsMinor { get; set; }
    public DateTime ArrivalDate { get; set; }
    public int StayDays { get; set; }
    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public Gender? Gender { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public bool? BornInItaly { get; set; }
    public string BirthComune { get; set; } = string.Empty;
    public string? BirthProvince { get; set; }
    public string BirthCountry { get; set; } = string.Empty;
    public string Citizenship { get; set; } = string.Empty;
    public bool RequiresDocument { get; set; }
    public GuestDocumentType? DocumentType { get; set; }

    /// <summary>
    /// Document number with only its last characters visible (<c>*****567</c>), as on the guest portal (CO-02); null
    /// when none is on file. The full number: <c>GET /api/alloggiati/{bookingId}/stay-guests/document-numbers</c>.
    /// </summary>
    public string? DocumentNumberMasked { get; set; }

    public string DocumentIssuePlace { get; set; } = string.Empty;
    public AlloggiatiRowCodesDto Codes { get; set; } = new();
    public IReadOnlyList<string> MissingFields { get; set; } = [];
    public IReadOnlyList<string> CodesToComplete { get; set; } = [];
    public string? CompositionIssue { get; set; }

    /// <summary>Who entered the data (CO-09): <c>GuestPortal</c>, <c>Host</c>, or <c>NotRecorded</c> (booker record, or before CO-09).</summary>
    public StayGuestDataSource DataSource { get; set; }

    /// <summary>When the data was entered; null for the booker shown before any guest is registered.</summary>
    public DateTime? EnteredAt { get; set; }

    public static AlloggiatiGuestRowDto From(AlloggiatiGuestRow row) => new()
    {
        StayGuestId = row.StayGuestId,
        Position = row.Position,
        Type = row.Type,
        IsMinor = row.IsMinor,
        ArrivalDate = row.ArrivalDate,
        StayDays = row.StayDays,
        LastName = row.LastName,
        FirstName = row.FirstName,
        Gender = row.Gender,
        DateOfBirth = row.DateOfBirth,
        BornInItaly = row.BornInItaly,
        BirthComune = row.BirthComune,
        BirthProvince = row.BirthProvince,
        BirthCountry = row.BirthCountry,
        Citizenship = row.Citizenship,
        RequiresDocument = row.RequiresDocument,
        DocumentType = row.DocumentType,
        DocumentNumberMasked = GuestCheckInService.MaskDocumentNumber(row.DocumentNumber),
        DocumentIssuePlace = row.DocumentIssuePlace,
        Codes = new AlloggiatiRowCodesDto
        {
            Type = row.Codes.Type,
            BirthComune = row.Codes.BirthComune,
            BirthCountry = row.Codes.BirthCountry,
            Citizenship = row.Codes.Citizenship,
            DocumentType = row.Codes.DocumentType,
            DocumentTypeDescription = row.Codes.DocumentTypeDescription,
            DocumentIssuePlace = row.Codes.DocumentIssuePlace,
        },
        MissingFields = row.MissingFields,
        CodesToComplete = row.CodesToComplete,
        CompositionIssue = row.CompositionIssue,
        DataSource = row.DataSource,
        EnteredAt = row.EnteredAt,
    };
}

/// <summary>Official codes of a guest's record line (null = to complete or not part of the line).</summary>
public class AlloggiatiRowCodesDto
{
    public string? Type { get; set; }
    public string? BirthComune { get; set; }
    public string? BirthCountry { get; set; }
    public string? Citizenship { get; set; }
    public string? DocumentType { get; set; }
    public string? DocumentTypeDescription { get; set; }
    public string? DocumentIssuePlace { get; set; }
}

/// <summary>Full document number of a guest of the stay (CO-09: shown on an explicit request of the host, audited).</summary>
public class StayGuestDocumentNumberDto
{
    public int Position { get; set; }

    /// <summary>Null for the booker shown before any guest is registered.</summary>
    public Guid? StayGuestId { get; set; }

    public string DocumentNumber { get; set; } = string.Empty;
}

/// <summary>Body of <c>PUT /api/alloggiati/{bookingId}/stay-guests</c>: every guest of the stay, in record order.</summary>
public class ReplaceStayGuestsRequest
{
    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    public List<StayGuestSubmitDto> Guests { get; set; } = [];
}

/// <summary>One entry of an official Alloggiati table.</summary>
public class AlloggiatiCodeEntryDto
{
    public AlloggiatiCodeTable Table { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Province { get; set; }

    public static AlloggiatiCodeEntryDto From(AlloggiatiCodeEntryInfo info) => new()
    {
        Table = info.Table,
        Code = info.Code,
        Description = info.Description,
        Province = info.Province,
    };
}

/// <summary>Rows and last import of an official Alloggiati table.</summary>
public class AlloggiatiCodeTableStatusDto
{
    public AlloggiatiCodeTable Table { get; set; }
    public int RowCount { get; set; }
    public DateTime? ImportedAt { get; set; }
    public string? SourceVersion { get; set; }
    public string? SourceFileName { get; set; }

    public static AlloggiatiCodeTableStatusDto From(AlloggiatiCodeTableStatus status) => new()
    {
        Table = status.Table,
        RowCount = status.RowCount,
        ImportedAt = status.ImportedAt,
        SourceVersion = status.SourceVersion,
        SourceFileName = status.SourceFileName,
    };
}

/// <summary>
/// Lists of official codes the forms can search: <c>comuni</c>, <c>stati</c> (states and citizenships),
/// <c>documenti</c> and <c>luoghi</c> (comuni and states, for the place of issue of a document).
/// </summary>
public static class AlloggiatiCodeLists
{
    public static bool TryParse(string? list, out IReadOnlyList<AlloggiatiCodeTable> tables)
    {
        tables = list?.Trim().ToLowerInvariant() switch
        {
            "comuni" => [AlloggiatiCodeTable.Comuni],
            "stati" => [AlloggiatiCodeTable.Stati],
            "documenti" => [AlloggiatiCodeTable.Documenti],
            "luoghi" => [AlloggiatiCodeTable.Comuni, AlloggiatiCodeTable.Stati],
            _ => [],
        };
        return tables.Count > 0;
    }
}
