using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.CheckIn;

namespace Casazen.Web.DTOs.Alloggiati;

/// <summary>
/// The guests of a stay on the channel manager and the receipt that covers its arrival day (E20/F01/US01/T02): the block the per-stay Alloggiati
/// status (<c>GET api/alloggiati/{bookingId}/status</c>) and the compliance of a booking (<c>GET api/bookings/{bookingId}/compliance</c>) share, built from
/// the same facts and rules as the cockpit. No personal data: positions, states and codes.
/// </summary>
/// <param name="Sender">Who sends the police communication of the stay, as far as CasaZen can rely on it (<c>ChannelManager</c> only while the connector is on).</param>
/// <param name="State">Where the guests stand on the channel manager: <c>Entrusted</c> (every guest on it for good), <c>Failed</c>, <c>InProgress</c>, <c>NothingWritten</c> or <c>NotApplicable</c>.</param>
/// <param name="Guests">The guests written to the channel manager, by position: state, error code, the fields to correct, attempts and last attempt. Empty when it is not the sender.</param>
/// <param name="ReceiptCoverage">Whether an archived receipt covers the arrival day. Green exists only with one.</param>
/// <param name="Light">The light of the cockpit for the stay (<c>Yellow</c> for an entrusted stay without a receipt, never <c>Green</c> without one); <c>null</c> before the arrival day or when nothing is due.</param>
/// <param name="Reason">The stable reason of the light (the clients translate it); <c>null</c> with no light.</param>
/// <param name="Actions">What can help: <c>RetryOctorateWrite</c> when asking again can mend a write that failed.</param>
public sealed record AlloggiatiChannelManagerDto(
    ComplianceSenderType Sender,
    ChannelManagerWriteState State,
    IReadOnlyList<AlloggiatiChannelManagerGuestDto> Guests,
    AlloggiatiReceiptCoverageDto ReceiptCoverage,
    ComplianceLight? Light,
    string? Reason,
    IReadOnlyList<string> Actions)
{
    public static AlloggiatiChannelManagerDto From(ComplianceStayStatus stay)
    {
        ArgumentNullException.ThrowIfNull(stay);
        return new AlloggiatiChannelManagerDto(
            stay.Sender,
            stay.ChannelManager,
            stay.Guests.Select(AlloggiatiChannelManagerGuestDto.From).ToList(),
            AlloggiatiReceiptCoverageDto.From(stay.ReceiptProof),
            stay.Verdict?.Verdict.Light,
            stay.Verdict?.Verdict.Reason,
            stay.Actions);
    }
}

/// <summary>One guest of a stay as it stands on the channel manager. No name, no document: the position, the state and the codes.</summary>
/// <param name="Position">The position of the guest in the stay.</param>
/// <param name="State"><c>Pending</c>, <c>Sent</c> or <c>Failed</c>.</param>
/// <param name="SentAsDraft">Written as a draft (the sender was not the channel manager): not entrusted.</param>
/// <param name="ErrorCode">Why the last attempt failed (<c>place_not_recognized</c>, <c>octorate_auth</c>, <c>octorate_guest_rejected</c>, ...); <c>null</c> after a success.</param>
/// <param name="ErrorFields">The fields of the guest to correct for <c>place_not_recognized</c> (<c>birthComune</c>, <c>residenceComune</c>, <c>documentIssuePlace</c>, comma separated); <c>null</c> otherwise.</param>
/// <param name="Attempts">The attempts of the payload last tried.</param>
/// <param name="LastAttemptAt">When the last attempt was made (UTC).</param>
public sealed record AlloggiatiChannelManagerGuestDto(
    int Position,
    OctorateSyncStatus State,
    bool SentAsDraft,
    string? ErrorCode,
    string? ErrorFields,
    int Attempts,
    DateTime? LastAttemptAt)
{
    public static AlloggiatiChannelManagerGuestDto From(ChannelManagerGuestFact guest)
    {
        ArgumentNullException.ThrowIfNull(guest);
        return new AlloggiatiChannelManagerGuestDto(
            guest.Position, guest.Status, guest.SentAsDraft, guest.ErrorCode, guest.ErrorFields, guest.Attempts, guest.LastAttemptAt);
    }
}

public sealed record AlloggiatiReceiptCoverageDto(bool Archived, string? Source, DateTime? ArchivedAt)
{
    public static AlloggiatiReceiptCoverageDto From(ComplianceProof? proof) =>
        proof is null ? new AlloggiatiReceiptCoverageDto(false, null, null) : new AlloggiatiReceiptCoverageDto(true, proof.Source, proof.VerifiedAt);
}

/// <summary>
/// What the host's retry of the write of the guests did (<c>POST api/alloggiati/{bookingId}/retry-octorate-write</c>, E20/F01/US01/T02): the outcome of the
/// send and, when it did nothing or not everything, why. No personal data.
/// </summary>
/// <param name="State">
/// <c>Sent</c> (every guest tried went), <c>PartlyFailed</c> or <c>Failed</c> (the guests that could not be written have their code in the block of
/// the guests), <c>NothingToSend</c> (no guest could be tried again: a guest held back for a place is corrected, not retried) or <c>NotApplicable</c> /
/// <c>NotMirrored</c> (the stay is not written to the channel manager at all).
/// </param>
/// <param name="Reason">Why nothing was tried, a stable code the clients translate (<c>sender_not_channel_manager</c>, <c>booking_not_active</c>, ...); <c>null</c> when guests were tried.</param>
public sealed record AlloggiatiRetryResultDto(OctorateCheckInSendState State, string? Reason)
{
    public static AlloggiatiRetryResultDto From(OctorateCheckInSendResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new AlloggiatiRetryResultDto(result.State, result.Reason);
    }
}

public class AlloggiatiStatusDto
{
    public IReadOnlyList<DirectGuestOutcomeDto> DirectGuests { get; set; } = [];
    public bool DirectSubmission { get; set; }
    public string? DirectAttempt { get; set; }
    public bool CanDownloadDirectReceipt { get; set; }

    public Guid BookingId { get; set; }

    /// <summary>
    /// <see cref="AlloggiatiWebStatus.NotDue"/> for a stay that owes nothing (NF-45: a no-show, a request still waiting, a cancelled booking): the guests never
    /// came, nothing is to be communicated. For a stay whose sender is the channel manager,
    /// <see cref="AlloggiatiWebStatus.EntrustedToChannelManager"/> once its guests are on Octorate (yellow, never green without an archived receipt) and
    /// <see cref="AlloggiatiWebStatus.ChannelManagerWriteFailed"/> when a guest could not be written (E20/F01/US01/T02).
    /// </summary>
    public AlloggiatiWebStatus Status { get; set; }

    public string? ConfirmationNumber { get; set; }

    public string? ErrorCode { get; set; }

    public DateTime? ReportedAt { get; set; }

    public DateTime DeadlineAt { get; set; }

    public bool IsShortStay { get; set; }
    public double HoursUntilDeadline { get; set; }
    public bool IsOverdue { get; set; }
    public bool DataComplete { get; set; }

    public AlloggiatiChannelManagerDto? ChannelManager { get; set; }

    /// <summary>What the host's retry of the write of the guests did; <c>null</c> in every other answer (E20/F01/US01/T02).</summary>
    public AlloggiatiRetryResultDto? Retry { get; set; }

    public static AlloggiatiStatusDto From(AlloggiatiStatusInfo info, ComplianceStayStatus? stay = null) => new()
    {
        BookingId = info.BookingId,
        Status = StatusOf(info, stay),
        ConfirmationNumber = info.ConfirmationNumber,
        ErrorCode = info.ErrorCode,
        ReportedAt = info.ReportedAt,
        DeadlineAt = info.DeadlineAt,
        IsShortStay = info.IsShortStay,
        HoursUntilDeadline = info.HoursUntilDeadline,
        IsOverdue = info.IsOverdue,
        DataComplete = info.DataComplete,
        ChannelManager = stay is null ? null : AlloggiatiChannelManagerDto.From(stay),
    };

    private static AlloggiatiWebStatus StatusOf(AlloggiatiStatusInfo info, ComplianceStayStatus? stay)
    {
        if (stay is null)
            return info.Status;

        // A stay that owes nothing reads «not due» whatever its guests are on the channel manager, unless its communication was sent.
        if (stay.Status == AlloggiatiWebStatus.NotDue && !AlloggiatiStatusRules.IsSent(info.Status))
            return AlloggiatiWebStatus.NotDue;

        return AlloggiatiStatusRules.WithChannelManager(info.Status, stay.ChannelManager);
    }
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

    public bool StayExceedsMaxDays { get; set; }

    public int DeclaredGuests { get; set; }

    public bool DataComplete { get; set; }

    public bool ExportReady { get; set; }

    public IReadOnlyList<AlloggiatiCodeTable> MissingCodeTables { get; set; } = [];

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

public class AlloggiatiGuestProgressDto
{
    public Guid BookingId { get; set; }

    public int DeclaredGuests { get; set; }

    public int RegisteredGuests { get; set; }

    public int CompleteGuests { get; set; }

    public bool DataComplete { get; set; }

    public bool StayExceedsMaxDays { get; set; }

    public static AlloggiatiGuestProgressDto From(AlloggiatiGuestSummaryInfo info) => new()
    {
        BookingId = info.BookingId,
        DeclaredGuests = info.DeclaredGuests,
        RegisteredGuests = info.Guests.Count,
        CompleteGuests = info.Guests.Count(g => g.MissingFields.Count == 0 && g.CompositionIssue is null),
        DataComplete = info.DataComplete,
        StayExceedsMaxDays = info.StayExceedsMaxDays,
    };
}

/// <summary>
/// One guest, fields in the order of the Alloggiati Web record. Text as stored; <see cref="Codes"/> are the official
/// codes found in the imported tables (null = to complete or not part of the line).
/// </summary>
public class AlloggiatiGuestRowDto
{
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

    public StayGuestDataSource DataSource { get; set; }

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

public class StayGuestDocumentNumberDto
{
    public int Position { get; set; }

    public Guid? StayGuestId { get; set; }

    public string DocumentNumber { get; set; } = string.Empty;
}

/// <summary>
/// Body of <c>PUT /api/alloggiati/{bookingId}/stay-guests</c> and <c>PUT /api/bookings/{id}/checkin-wizard/guests</c>: every guest of the stay, in record
/// order, with their home (NF-132: <c>residenceStreet</c>, <c>residencePostalCode</c>, <c>residenceCityName</c>, whole truth like every field of a guest).
/// </summary>
public class ReplaceStayGuestsRequest
{
    [Required(ErrorMessage = CheckInValidationKeys.FieldRequired)]
    public List<StayGuestSubmitDto> Guests { get; set; } = [];

    /// <summary>
    /// The plate of the car of the stay (NF-132, «Numero autovettura» of the Ticino police file). Not sent (<c>null</c>): the stored plate stays, so a
    /// client that does not know the field never clears it; an empty text clears it. Spaces are removed and the letters upper-cased; letters, digits
    /// and hyphens only, at most 20 (field <c>VehiclePlate</c>, <c>CheckInVehiclePlateInvalid</c>).
    /// </summary>
    [MaxLength(64, ErrorMessage = CheckInValidationKeys.FieldMaxLength)]
    public string? VehiclePlate { get; set; }
}

public class AlloggiatiCodeEntryDto
{
    public AlloggiatiCodeTable Table { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Province { get; set; }

    public string? CitizenshipLabel { get; set; }

    public static AlloggiatiCodeEntryDto From(AlloggiatiCodeEntryInfo info) => new()
    {
        Table = info.Table,
        Code = info.Code,
        Description = info.Description,
        Province = info.Province,
        CitizenshipLabel = info.CitizenshipLabel,
    };
}

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

public sealed record DirectGuestOutcomeDto(int Position, string Outcome);
