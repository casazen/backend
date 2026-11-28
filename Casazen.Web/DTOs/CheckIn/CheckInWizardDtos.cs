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
/// The reason of a tourist tax exemption (on the guests and on the tax) is shown only to a caller who holds <c>guest.read</c>: it can reveal a
/// health condition (art. 9 GDPR), and the accountant role reads the bookings without it.
/// </summary>
public sealed class CheckInWizardStateDto
{
    public CheckInWizardBookingDto Booking { get; init; } = new();

    public StayAttendance Attendance { get; init; }

    public IReadOnlyList<CheckInWizardGuestDto> Guests { get; init; } = [];

    public IReadOnlyList<string> RequiredGuestFields { get; init; } = [];

    public bool AskVehiclePlate { get; init; }

    public IReadOnlyList<CheckInIssueDto> Issues { get; init; } = [];

    public StayTouristTaxDto? Tax { get; init; }

    public CheckInWizardSenderDto Sender { get; init; } = new();

    public CheckInWizardOctorateDto Octorate { get; init; } = new();

    public CheckInWizardAlloggiatiDto Alloggiati { get; init; } = new();

    public CheckInWizardCapabilitiesDto Capabilities { get; init; } = new();

    public string SuggestedStep { get; init; } = CheckInWizardSteps.Guests;

    /// <param name="state">What the six steps show.</param>
    /// <param name="includeExemptionCodes">
    /// False for a caller who does not hold <c>guest.read</c>: the reason of a tourist tax exemption (on the guests and on the tax) can reveal a
    /// health condition (art. 9 GDPR), so it is <c>null</c>, as the full document numbers are not shown either.
    /// </param>
    public static CheckInWizardStateDto From(CheckInState state, bool includeExemptionCodes) => new()
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
            VehiclePlate = state.Booking.VehiclePlate,
        },
        Attendance = state.Attendance,
        Guests = state.Guests.Select(guest => CheckInWizardGuestDto.From(guest, includeExemptionCodes)).ToList(),
        RequiredGuestFields = state.RequiredGuestFields,
        AskVehiclePlate = state.AskVehiclePlate,
        Issues = state.Issues.Select(CheckInIssueDto.From).ToList(),
        Tax = state.Tax is { } tax ? StayTouristTaxDto.From(tax, includeExemptionCodes) : null,
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

    public string? VehiclePlate { get; init; }
}

public sealed class CheckInWizardGuestDto
{
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

    public string? ResidenceStreet { get; init; }
    public string? ResidencePostalCode { get; init; }
    public string? ResidenceCityName { get; init; }
    public GuestDocumentType? DocumentType { get; init; }
    public string? DocumentTypeCode { get; init; }

    public string? DocumentNumberMasked { get; init; }

    public string? DocumentIssuePlaceCode { get; init; }
    public string DocumentIssuePlaceName { get; init; } = string.Empty;
    public DateOnly? DocumentIssuedOn { get; init; }
    public DateOnly? DocumentExpiresOn { get; init; }
    public string? TourismType { get; init; }
    public string? TravelWay { get; init; }
    public string? EducationLevel { get; init; }

    public string? TaxExemptionCode { get; init; }

    public StayGuestDataSource DataSource { get; init; }

    public static CheckInWizardGuestDto From(StayGuest guest, bool includeExemptionCode) => new()
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
        ResidenceStreet = guest.ResidenceStreet,
        ResidencePostalCode = guest.ResidencePostalCode,
        ResidenceCityName = guest.ResidenceCityName,
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
        TaxExemptionCode = includeExemptionCode ? guest.TaxExemptionCode : null,
        DataSource = guest.DataSource,
    };
}

public sealed class CheckInIssueDto
{
    public int? Position { get; init; }

    public string Code { get; init; } = string.Empty;

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

public sealed class CheckInWizardSenderDto
{
    public ComplianceSenderType AlloggiatiSender { get; init; }

    public SenderSource Source { get; init; }

    public bool Applies { get; init; }
}

public sealed class CheckInWizardOctorateDto
{
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

public sealed class CheckInWizardAlloggiatiDto
{
    public string Status { get; init; } = string.Empty;
    public bool DataComplete { get; init; }
    public DateTime DeadlineAt { get; init; }
    public bool IsOverdue { get; init; }
    public DateTime? ReportedAt { get; init; }
}

public sealed class CheckInWizardCapabilitiesDto
{
    public bool DocumentReading { get; init; }

    public bool CanRegisterArrival { get; init; }
    public bool CanMarkNoShow { get; init; }

    public bool OctorateTaxValues { get; init; }
}

public sealed class CheckInConfirmationDto
{
    public CheckInTaxOutcomeDto Tax { get; init; } = new();
    public CheckInOctorateOutcomeDto Octorate { get; init; } = new();
    public CheckInAlloggiatiNextDto Alloggiati { get; init; } = new();
    public CheckInWizardStateDto State { get; init; } = new();

    public static CheckInConfirmationDto From(CheckInConfirmation confirmation, bool includeExemptionCodes) => new()
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
        State = CheckInWizardStateDto.From(confirmation.State, includeExemptionCodes),
    };
}

public sealed class CheckInTaxOutcomeDto
{
    public bool Saved { get; init; }
    public string? Reason { get; init; }
}

public sealed class CheckInOctorateOutcomeDto
{
    public OctorateCheckInSendState State { get; init; }
    public string? Reason { get; init; }
    public bool Draft { get; init; }
    public bool CorrectionAfterSend { get; init; }
    public IReadOnlyList<OctorateGuestSendResult> Guests { get; init; } = [];
}

public sealed class CheckInAlloggiatiNextDto
{
    public CheckInAlloggiatiNextStep Step { get; init; }

    public ComplianceSenderType Sender { get; init; }
}

public sealed class DocumentReadingDto
{
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
