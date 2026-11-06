using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.TouristTax;

namespace Casazen.Web.DTOs.CheckIn;

/// <summary>
/// The host wizard "Guest check-in" (<c>GET api/bookings/{id}/checkin-wizard</c>, E03/F01/US01/T04): the six steps from one answer. The
/// document numbers are masked; the full ones stay on <c>GET api/alloggiati/{bookingId}/stay-guests/document-numbers</c> (guest.read, audited).
/// </summary>
public sealed class CheckInWizardStateDto
{
    public CheckInWizardBookingDto Booking { get; init; } = new();

    /// <summary><c>Expected</c>, <c>Arrived</c>, <c>Departed</c>, <c>NoShow</c> or <c>Inactive</c>.</summary>
    public StayAttendance Attendance { get; init; }

    public IReadOnlyList<CheckInWizardGuestDto> Guests { get; init; } = [];

    /// <summary>The statistical fields the region requires (none in this release).</summary>
    public IReadOnlyList<string> RequiredGuestFields { get; init; } = [];

    public IReadOnlyList<CheckInIssueDto> Issues { get; init; } = [];

    /// <summary>The tourist tax of the stay; null when it cannot be worked out now.</summary>
    public StayTouristTaxDto? Tax { get; init; }

    public CheckInWizardSenderDto Sender { get; init; } = new();

    public CheckInWizardOctorateDto Octorate { get; init; } = new();

    public CheckInWizardAlloggiatiDto Alloggiati { get; init; } = new();

    public CheckInWizardCapabilitiesDto Capabilities { get; init; } = new();

    /// <summary><c>guests</c>, <c>documents</c>, <c>checks</c>, <c>tax</c>, <c>send</c> or <c>result</c>: the first step with something to do.</summary>
    public string SuggestedStep { get; init; } = CheckInWizardSteps.Guests;

    public static CheckInWizardStateDto From(CheckInState state) => new()
    {
        Booking = new CheckInWizardBookingDto
        {
            BookingId = state.Booking.BookingId,
            PropertyId = state.Booking.PropertyId,
            PropertyName = state.Booking.PropertyName,
            CheckIn = state.Booking.CheckIn,
            CheckOut = state.Booking.CheckOut,
            Nights = state.Booking.Nights,
            Source = state.Booking.Source.ToString(),
            DeclaredGuests = state.Booking.DeclaredGuests,
            Status = state.Booking.Status.ToString(),
            NoShowAt = state.Booking.NoShowAt,
        },
        Attendance = state.Attendance,
        Guests = state.Guests.Select(CheckInWizardGuestDto.From).ToList(),
        RequiredGuestFields = state.RequiredGuestFields,
        Issues = state.Issues.Select(CheckInIssueDto.From).ToList(),
        Tax = state.Tax is { } tax ? StayTouristTaxDto.From(tax) : null,
        Sender = new CheckInWizardSenderDto
        {
            AlloggiatiSender = state.Sender.AlloggiatiSender,
            Source = state.Sender.Source,
            Applies = state.Sender.Applies,
        },
        Octorate = CheckInWizardOctorateDto.From(state.Octorate),
        Alloggiati = new CheckInWizardAlloggiatiDto
        {
            Status = state.Alloggiati.Status.ToString(),
            DataComplete = state.Alloggiati.DataComplete,
            DeadlineAt = state.Alloggiati.DeadlineAt,
            IsOverdue = state.Alloggiati.IsOverdue,
            ReportedAt = state.Alloggiati.ReportedAt,
        },
        Capabilities = new CheckInWizardCapabilitiesDto
        {
            DocumentReading = state.Capabilities.DocumentReading,
            CanRegisterArrival = state.Capabilities.CanRegisterArrival,
            CanMarkNoShow = state.Capabilities.CanMarkNoShow,
            OctorateTaxValues = state.Capabilities.OctorateTaxValues,
        },
        SuggestedStep = state.SuggestedStep,
    };
}

public sealed class CheckInWizardBookingDto
{
    public Guid BookingId { get; init; }
    public Guid PropertyId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
    public int Nights { get; init; }
    public string Source { get; init; } = string.Empty;
    public int DeclaredGuests { get; init; }
    public string Status { get; init; } = string.Empty;

    /// <summary>When the host marked the stay as a no-show; null otherwise.</summary>
    public DateTime? NoShowAt { get; init; }
}

/// <summary>A guest as the host summary shows it: the document number masked, the exemption code of the tourist tax with the guest.</summary>
public sealed class CheckInWizardGuestDto
{
    /// <summary>False for the booker shown before any guest is registered (nothing saved yet).</summary>
    public bool Saved { get; init; }

    public int Position { get; init; }
    public StayGuestType Type { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public Gender? Gender { get; init; }
    public DateTime? DateOfBirth { get; init; }
    public bool? BornInItaly { get; init; }
    public string? BirthComuneCode { get; init; }
    public string BirthComuneName { get; init; } = string.Empty;
    public string? BirthProvince { get; init; }
    public string? BirthCountryCode { get; init; }
    public string BirthCountryName { get; init; } = string.Empty;
    public string? CitizenshipCode { get; init; }
    public string CitizenshipName { get; init; } = string.Empty;
    public bool? ResidenceInItaly { get; init; }
    public string? ResidenceComuneCode { get; init; }
    public string? ResidenceComuneName { get; init; }
    public string? ResidenceProvince { get; init; }
    public string? ResidenceCountryCode { get; init; }
    public string? ResidenceCountryName { get; init; }
    public GuestDocumentType? DocumentType { get; init; }
    public string? DocumentTypeCode { get; init; }

    /// <summary>The document number with all but the last characters masked; null without one.</summary>
    public string? DocumentNumberMasked { get; init; }

    public string? DocumentIssuePlaceCode { get; init; }
    public string DocumentIssuePlaceName { get; init; } = string.Empty;
    public DateOnly? DocumentIssuedOn { get; init; }
    public DateOnly? DocumentExpiresOn { get; init; }
    public string? TourismType { get; init; }
    public string? TravelWay { get; init; }
    public string? EducationLevel { get; init; }

    /// <summary>The host's exemption of the tourist tax (a reason code), if any.</summary>
    public string? TaxExemptionCode { get; init; }

    public StayGuestDataSource DataSource { get; init; }

    public static CheckInWizardGuestDto From(StayGuest guest) => new()
    {
        Saved = guest.Id != Guid.Empty,
        Position = guest.Position,
        Type = guest.Type,
        FirstName = guest.FirstName,
        LastName = guest.LastName,
        Gender = guest.Gender,
        DateOfBirth = guest.DateOfBirth,
        BornInItaly = guest.BornInItaly,
        BirthComuneCode = guest.BirthComuneCode,
        BirthComuneName = guest.BirthComuneName,
        BirthProvince = guest.BirthProvince,
        BirthCountryCode = guest.BirthCountryCode,
        BirthCountryName = guest.BirthCountryName,
        CitizenshipCode = guest.CitizenshipCode,
        CitizenshipName = guest.CitizenshipName,
        ResidenceInItaly = guest.ResidenceInItaly,
        ResidenceComuneCode = guest.ResidenceComuneCode,
        ResidenceComuneName = guest.ResidenceComuneName,
        ResidenceProvince = guest.ResidenceProvince,
        ResidenceCountryCode = guest.ResidenceCountryCode,
        ResidenceCountryName = guest.ResidenceCountryName,
        DocumentType = guest.DocumentType,
        DocumentTypeCode = guest.DocumentTypeCode,
        DocumentNumberMasked = GuestCheckInService.MaskDocumentNumber(guest.DocumentNumber),
        DocumentIssuePlaceCode = guest.DocumentIssuePlaceCode,
        DocumentIssuePlaceName = guest.DocumentIssuePlaceName,
        DocumentIssuedOn = guest.DocumentIssuedOn,
        DocumentExpiresOn = guest.DocumentExpiresOn,
        TourismType = guest.TourismType,
        TravelWay = guest.TravelWay,
        EducationLevel = guest.EducationLevel,
        TaxExemptionCode = guest.TaxExemptionCode,
        DataSource = guest.DataSource,
    };
}

/// <summary>An issue of the checks: the code and the field the client translates.</summary>
public sealed class CheckInIssueDto
{
    /// <summary>The position of the guest; null for the stay as a whole.</summary>
    public int? Position { get; init; }

    /// <summary>
    /// <c>guest_field_missing</c>, <c>guest_code_missing</c>, <c>octorate_field_missing</c>, <c>guest_field_required</c>, <c>document_expired</c> or
    /// <c>guests_count_mismatch</c>.
    /// </summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>The camelCase field of the guest; null for the stay.</summary>
    public string? Field { get; init; }

    /// <summary>True when the wizard cannot be confirmed until it is fixed; false for a warning.</summary>
    public bool Blocking { get; init; }

    public static CheckInIssueDto From(CheckInIssue issue) => new()
    {
        Position = issue.Position,
        Code = issue.Code,
        Field = issue.Field,
        Blocking = issue.Blocking,
    };
}

/// <summary>Who sends the police communications of the property.</summary>
public sealed class CheckInWizardSenderDto
{
    /// <summary><c>ChannelManager</c>, <c>CasaZen</c> or <c>Manual</c>.</summary>
    public ComplianceSenderType AlloggiatiSender { get; init; }

    public SenderSource Source { get; init; }

    /// <summary>False for a property outside Italy.</summary>
    public bool Applies { get; init; }
}

/// <summary>The check-in on Octorate: each guest sent and the three states of the stay (no personal data).</summary>
public sealed class CheckInWizardOctorateDto
{
    /// <summary>False when the booking is not on Octorate (then the lists are empty).</summary>
    public bool Mirrored { get; init; }

    public IReadOnlyList<OctorateGuestLinkView> Guests { get; init; } = [];

    public IReadOnlyList<OctorateStayStateView> States { get; init; } = [];

    public static CheckInWizardOctorateDto From(OctorateCheckInView view) => new()
    {
        Mirrored = view.Mirrored,
        Guests = view.Guests,
        States = view.States,
    };
}

/// <summary>The police communication of the stay.</summary>
public sealed class CheckInWizardAlloggiatiDto
{
    public string Status { get; init; } = string.Empty;
    public bool DataComplete { get; init; }
    public DateTime DeadlineAt { get; init; }
    public bool IsOverdue { get; init; }
    public DateTime? ReportedAt { get; init; }
}

/// <summary>What the host can do now.</summary>
public sealed class CheckInWizardCapabilitiesDto
{
    /// <summary>The photo of a document can be read (<c>POST …/checkin-wizard/document-reading</c>; 404 while off).</summary>
    public bool DocumentReading { get; init; }

    public bool CanRegisterArrival { get; init; }
    public bool CanMarkNoShow { get; init; }

    /// <summary>The tourist tax values travel to Octorate with the guests.</summary>
    public bool OctorateTaxValues { get; init; }
}

/// <summary>The answer of <c>POST api/bookings/{id}/checkin-wizard/confirm</c>: one entry per element and the new state.</summary>
public sealed class CheckInConfirmationDto
{
    public CheckInTaxOutcomeDto Tax { get; init; } = new();
    public CheckInOctorateOutcomeDto Octorate { get; init; } = new();
    public CheckInAlloggiatiNextDto Alloggiati { get; init; } = new();
    public CheckInWizardStateDto State { get; init; } = new();

    public static CheckInConfirmationDto From(CheckInConfirmation confirmation) => new()
    {
        Tax = new CheckInTaxOutcomeDto { Saved = confirmation.Tax.Saved, Reason = confirmation.Tax.Reason },
        Octorate = new CheckInOctorateOutcomeDto
        {
            State = confirmation.Octorate.State,
            Reason = confirmation.Octorate.Reason,
            Draft = confirmation.Octorate.Draft,
            CorrectionAfterSend = confirmation.Octorate.CorrectionAfterSend,
            Guests = confirmation.Octorate.Guests,
        },
        Alloggiati = new CheckInAlloggiatiNextDto { Step = confirmation.Alloggiati.Step, Sender = confirmation.Alloggiati.Sender },
        State = CheckInWizardStateDto.From(confirmation.State),
    };
}

/// <summary>The tourist tax of the confirmation: saved, or the code of why not (<c>tax_not_calculable</c>, ...).</summary>
public sealed class CheckInTaxOutcomeDto
{
    public bool Saved { get; init; }
    public string? Reason { get; init; }
}

/// <summary>What was sent to Octorate; <c>correctionAfterSend</c> warns that data already transmitted may need a correction on the portal.</summary>
public sealed class CheckInOctorateOutcomeDto
{
    public OctorateCheckInSendState State { get; init; }
    public string? Reason { get; init; }
    public bool Draft { get; init; }
    public bool CorrectionAfterSend { get; init; }
    public IReadOnlyList<OctorateGuestSendResult> Guests { get; init; } = [];
}

/// <summary>What the police communication does next for the sender of the property.</summary>
public sealed class CheckInAlloggiatiNextDto
{
    /// <summary><c>ChannelManagerSends</c>, <c>Manual</c> (with the guide), <c>AlreadySent</c> or <c>NotApplicable</c>.</summary>
    public CheckInAlloggiatiNextStep Step { get; init; }

    public ComplianceSenderType Sender { get; init; }
}

/// <summary>The fields read on a document photo (never stored): the host checks them before saving the guest.</summary>
public sealed class DocumentReadingDto
{
    /// <summary><c>Mrz</c>, <c>Ocr</c> or <c>None</c>.</summary>
    public DocumentReadingMethod Method { get; init; }

    public double Confidence { get; init; }
    public bool? CheckDigitsValid { get; init; }
    public bool RequiresManualReview { get; init; }
    public DocumentReadingFields? Fields { get; init; }

    public static DocumentReadingDto From(DocumentReading reading) => new()
    {
        Method = reading.Method,
        Confidence = reading.Confidence,
        CheckDigitsValid = reading.CheckDigitsValid,
        RequiresManualReview = reading.RequiresManualReview,
        Fields = reading.Fields,
    };
}

/// <summary>The answer of <c>POST api/bookings/{id}/no-show</c>.</summary>
public sealed class NoShowResultDto
{
    public Guid BookingId { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime? NoShowAt { get; init; }

    public static NoShowResultDto From(Booking booking) => new()
    {
        BookingId = booking.Id,
        Status = booking.Status.ToString(),
        NoShowAt = booking.NoShowAt,
    };
}
