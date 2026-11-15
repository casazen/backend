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

/// <summary>Whether an archived receipt covers the arrival day of the stay: a receipt CasaZen downloaded, or one the host uploaded.</summary>
/// <param name="Archived"><c>true</c> when a receipt is archived: the only thing that makes the stay green.</param>
/// <param name="Source"><c>casazen_receipt</c> or <c>manual_upload</c>; <c>null</c> when none.</param>
/// <param name="ArchivedAt">When it was archived (UTC); <c>null</c> when none or when it has no date.</param>
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
    public Guid BookingId { get; set; }

    /// <summary>
    /// <see cref="AlloggiatiWebStatus.NotDue"/> for a stay that owes nothing (NF-45: a no-show, a request still waiting, a cancelled booking): the guests never
    /// came, nothing is to be communicated. For a stay whose sender is the channel manager,
    /// <see cref="AlloggiatiWebStatus.EntrustedToChannelManager"/> once its guests are on Octorate (yellow, never green without an archived receipt) and
    /// <see cref="AlloggiatiWebStatus.ChannelManagerWriteFailed"/> when a guest could not be written (E20/F01/US01/T02).
    /// </summary>
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

    /// <summary>
    /// The guests of the stay on the channel manager, the sender and the receipt coverage (E20/F01/US01/T02); <c>null</c> where the status is answered
    /// without it (the declaration of a manual send).
    /// </summary>
    public AlloggiatiChannelManagerDto? ChannelManager { get; set; }

    /// <summary>What the host's retry of the write of the guests did; <c>null</c> in every other answer (E20/F01/US01/T02).</summary>
    public AlloggiatiRetryResultDto? Retry { get; set; }

    /// <summary>
    /// The status of a stay. With <paramref name="stay"/> (the same facts and rules as the cockpit) the channel manager is taken into account: the
    /// status reads «entrusted» or «write failed» where the guests say so, «not due» for a stay that owes nothing, and the block of the guests and the
    /// receipt coverage is added.
    /// </summary>
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
/// How far the guest data of a stay are (MO-08, A6-17): counts only, no personal data, for the host app booking detail
/// ("2 ospiti completi su 3"). Same rules as <see cref="AlloggiatiGuestSummaryDto"/>, which carries the data themselves.
/// </summary>
public class AlloggiatiGuestProgressDto
{
    public Guid BookingId { get; set; }

    /// <summary>Guests declared on the booking.</summary>
    public int DeclaredGuests { get; set; }

    /// <summary>
    /// Guests with a record line: the registered guests of the stay, or the booker alone while none is registered
    /// (the rows of the guest summary).
    /// </summary>
    public int RegisteredGuests { get; set; }

    /// <summary>Guests whose line has every record field and fits in the order of the stay.</summary>
    public int CompleteGuests { get; set; }

    /// <summary>
    /// Every registered guest is complete and the order of the guests is valid (<see cref="AlloggiatiGuestSummaryDto.DataComplete"/>).
    /// Declared guests not registered yet are <see cref="DeclaredGuests"/> minus <see cref="RegisteredGuests"/>.
    /// </summary>
    public bool DataComplete { get; set; }

    /// <summary>The stay is longer than the 30 days the portal accepts on one schedina.</summary>
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

    /// <summary>Citizenship adjective (Italiano). Null for a place and for a country without one.</summary>
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
