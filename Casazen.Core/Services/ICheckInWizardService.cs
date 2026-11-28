using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// The host wizard "Guest check-in" (guests, documents, checks, tax, send, result; E03/F01/US01/T04, NF-40, NF-45): one backend that composes
/// what the other services do, without a second copy of any rule. The guests are <see cref="IStayGuestService"/>'s, the tax
/// <see cref="IStayTouristTaxService"/>'s, the channel manager <see cref="ICheckInOctorateSyncService"/>'s and the sender
/// <see cref="IPropertySenderStore"/>'s. It records no <c>CheckInCompleted</c> or <c>CheckOutCompleted</c> (the stay lifecycle does), changes
/// no booking status and sends nothing to the police: the arrival and the check-out keep their own endpoints.
/// </summary>
/// <remarks>
/// A booking the caller's org does not have is not found (<see cref="Exceptions.NotFoundException"/>, the tenant filter); the web layer authorizes
/// the booking first. In this release CasaZen does not send to the police itself (E03/F01/US06 deferred), and the statistical fields a region requires
/// are the ones of its row of the ISTAT region table (E21/F01/US01/T01): <see cref="CheckInState.RequiredGuestFields"/> is empty where the table has no
/// row, none is verified or the region requires none.
/// </remarks>
public interface ICheckInWizardService
{
    Task<CheckInState> GetStateAsync(Guid bookingId, CancellationToken cancellationToken);

    Task<CheckInSaveResult> SaveGuestsAsync(
        Guid bookingId,
        IReadOnlyList<StayGuestInput> guests,
        string userId,
        CancellationToken cancellationToken,
        string? vehiclePlate = null);

    /// <summary>
    /// Confirms the wizard: 422 <c>checkin_not_ready</c> with the blocking issues (extension <c>issues</c>) when any exists; otherwise confirms the
    /// tourist tax when it is calculable (a comune with no rate never blocks the check-in), sends the guests to Octorate and reports each element.
    /// </summary>
    Task<CheckInConfirmation> ConfirmAsync(Guid bookingId, string userId, CancellationToken cancellationToken);
}

public static class CheckInWizardSteps
{
    public const string Guests = "guests";
    public const string Documents = "documents";
    public const string Checks = "checks";
    public const string Tax = "tax";
    public const string Send = "send";
    public const string Result = "result";
}

public static class CheckInWizardCodes
{
    public const string NotReady = "checkin_not_ready";

    public const string IssuesExtension = "issues";
}

/// <summary>The stay as the wizard shows it.</summary>
/// <param name="Booking">The booking: property, dates, nights, source, declared guests, status.</param>
/// <param name="Attendance">Expected, arrived, departed, no-show or inactive.</param>
/// <param name="Guests">The guests in record order; the booker shown before any guest is registered has <see cref="StayGuest.Id"/> empty.</param>
/// <param name="RequiredGuestFields">
/// The guest fields the regime requires: the statistical fields the region requires, from the ISTAT region table (none when it says none or is not
/// verified), and in canton Ticino the names of <see cref="TicinoCheckInFields.Required"/> (NF-132).
/// </param>
/// <param name="Issues">What the checks found.</param>
/// <param name="Tax">The tourist tax of the stay; null when it cannot be worked out now.</param>
/// <param name="Sender">Who sends the police communications of the property.</param>
/// <param name="Octorate">The check-in on Octorate (read-only).</param>
/// <param name="Alloggiati">The Alloggiati communication of the stay.</param>
/// <param name="Capabilities">What the host can do now.</param>
/// <param name="SuggestedStep">The first step with something to do (<see cref="CheckInWizardSteps"/>).</param>
/// <param name="AskVehiclePlate">The plate of the car is asked for the stay (optional): the property is in canton Ticino (NF-132).</param>
public sealed record CheckInState(
    CheckInBookingView Booking,
    StayAttendance Attendance,
    IReadOnlyList<StayGuest> Guests,
    IReadOnlyList<string> RequiredGuestFields,
    IReadOnlyList<CheckInIssue> Issues,
    StayTouristTaxView? Tax,
    PropertySenderInfo Sender,
    OctorateCheckInView Octorate,
    AlloggiatiStatusInfo Alloggiati,
    CheckInCapabilities Capabilities,
    string SuggestedStep,
    bool AskVehiclePlate = false);

public sealed record CheckInBookingView(
    Guid BookingId,
    Guid PropertyId,
    string PropertyName,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Nights,
    BookingSource Source,
    int DeclaredGuests,
    BookingStatus Status,
    DateTime? NoShowAt,
    string? VehiclePlate = null);

/// <summary>What the host can do now.</summary>
/// <param name="DocumentReading">The reading of a document photo is available (<c>Features:CheckInIdentityVerification</c>).</param>
/// <param name="CanRegisterArrival">The arrival can be registered today (the rules of the stay lifecycle).</param>
/// <param name="CanMarkNoShow">The stay can be marked as a no-show now.</param>
/// <param name="OctorateTaxValues">The tourist tax values travel to Octorate with the guests (<c>Octorate:WriteCityTaxValues</c>, mirrored booking).</param>
public sealed record CheckInCapabilities(bool DocumentReading, bool CanRegisterArrival, bool CanMarkNoShow, bool OctorateTaxValues);

public sealed record CheckInSaveResult(IReadOnlyList<StayGuestFieldError> Errors, CheckInState? State)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>The outcome of <see cref="ICheckInWizardService.ConfirmAsync"/>, one entry per element.</summary>
/// <param name="Tax">The tourist tax: saved, or why not.</param>
/// <param name="Octorate">What was sent to Octorate (draft, correction after a send).</param>
/// <param name="Alloggiati">What the police communication does next for the sender of the property.</param>
/// <param name="State">The state of the wizard after the confirmation.</param>
public sealed record CheckInConfirmation(
    CheckInTaxOutcome Tax,
    OctorateCheckInSendResult Octorate,
    CheckInAlloggiatiNext Alloggiati,
    CheckInState State);

public sealed record CheckInTaxOutcome(bool Saved, string? Reason);

public enum CheckInAlloggiatiNextStep
{
    ChannelManagerSends = 0,

    /// <summary>The host sends it on the Alloggiati Web portal, with the guide (CasaZen does not send in this release).</summary>
    Manual = 1,

    AlreadySent = 2,

    NotApplicable = 3,
}

public sealed record CheckInAlloggiatiNext(CheckInAlloggiatiNextStep Step, ComplianceSenderType Sender);
