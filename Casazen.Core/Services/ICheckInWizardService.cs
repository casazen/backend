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
/// the booking first. In this release CasaZen does not send to the police itself (E03/F01/US06 deferred) and no region requires a statistical
/// field (E03/F01/US08 deferred): <see cref="CheckInState.RequiredGuestFields"/> is empty.
/// </remarks>
public interface ICheckInWizardService
{
    /// <summary>Everything the six steps show, with the step to open (<see cref="CheckInState.SuggestedStep"/>). Other tasks read it too.</summary>
    Task<CheckInState> GetStateAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the guests of the stay with the host as author (<see cref="IStayGuestService.ReplaceAsync"/>): the input is the whole truth, the
    /// exemption codes of the tourist tax included. Invalid data gives the field errors and saves nothing.
    /// </summary>
    Task<CheckInSaveResult> SaveGuestsAsync(
        Guid bookingId,
        IReadOnlyList<StayGuestInput> guests,
        string userId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Confirms the wizard: 422 <c>checkin_not_ready</c> with the blocking issues (extension <c>issues</c>) when any exists; otherwise confirms the
    /// tourist tax when it is calculable (a comune with no rate never blocks the check-in), sends the guests to Octorate and reports each element.
    /// </summary>
    Task<CheckInConfirmation> ConfirmAsync(Guid bookingId, string userId, CancellationToken cancellationToken);
}

/// <summary>The steps of the wizard, in order.</summary>
public static class CheckInWizardSteps
{
    public const string Guests = "guests";
    public const string Documents = "documents";
    public const string Checks = "checks";
    public const string Tax = "tax";
    public const string Send = "send";
    public const string Result = "result";
}

/// <summary>The codes of the wizard.</summary>
public static class CheckInWizardCodes
{
    /// <summary>The confirmation found blocking issues (422).</summary>
    public const string NotReady = "checkin_not_ready";

    /// <summary>The extension of the 422 that lists the blocking issues.</summary>
    public const string IssuesExtension = "issues";
}

/// <summary>The stay as the wizard shows it.</summary>
/// <param name="Booking">The booking: property, dates, nights, source, declared guests, status.</param>
/// <param name="Attendance">Expected, arrived, departed, no-show or inactive.</param>
/// <param name="Guests">The guests in record order; the booker shown before any guest is registered has <see cref="StayGuest.Id"/> empty.</param>
/// <param name="RequiredGuestFields">The statistical fields the region requires (none in this release).</param>
/// <param name="Issues">What the checks found.</param>
/// <param name="Tax">The tourist tax of the stay; null when it cannot be worked out now.</param>
/// <param name="Sender">Who sends the police communications of the property.</param>
/// <param name="Octorate">The check-in on Octorate (read-only).</param>
/// <param name="Alloggiati">The Alloggiati communication of the stay.</param>
/// <param name="Capabilities">What the host can do now.</param>
/// <param name="SuggestedStep">The first step with something to do (<see cref="CheckInWizardSteps"/>).</param>
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
    string SuggestedStep);

/// <summary>The booking of the wizard.</summary>
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
    DateTime? NoShowAt);

/// <summary>What the host can do now.</summary>
/// <param name="DocumentReading">The reading of a document photo is available (<c>Features:CheckInIdentityVerification</c>).</param>
/// <param name="CanRegisterArrival">The arrival can be registered today (the rules of the stay lifecycle).</param>
/// <param name="CanMarkNoShow">The stay can be marked as a no-show now.</param>
/// <param name="OctorateTaxValues">The tourist tax values travel to Octorate with the guests (<c>Octorate:WriteCityTaxValues</c>, mirrored booking).</param>
public sealed record CheckInCapabilities(bool DocumentReading, bool CanRegisterArrival, bool CanMarkNoShow, bool OctorateTaxValues);

/// <summary>The outcome of <see cref="ICheckInWizardService.SaveGuestsAsync"/>: the field errors, or the new state.</summary>
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

/// <summary>The tourist tax of the confirmation: <paramref name="Saved"/>, or the code of why not (<c>tax_not_calculable</c>, ...).</summary>
public sealed record CheckInTaxOutcome(bool Saved, string? Reason);

/// <summary>What happens next to the police communication of the stay.</summary>
public enum CheckInAlloggiatiNextStep
{
    /// <summary>The channel manager is the sender: Octorate transmits the guests sent with <c>draft = false</c>.</summary>
    ChannelManagerSends = 0,

    /// <summary>The host sends it on the Alloggiati Web portal, with the guide (CasaZen does not send in this release).</summary>
    Manual = 1,

    /// <summary>It was already sent (receipt or the host's declaration).</summary>
    AlreadySent = 2,

    /// <summary>
    /// There is no police communication: the property is outside Italy, or the stay owes none (a no-show, a request still waiting or a cancelled
    /// booking: the guests never came).
    /// </summary>
    NotApplicable = 3,
}

/// <summary>The police communication line of the confirmation.</summary>
public sealed record CheckInAlloggiatiNext(CheckInAlloggiatiNextStep Step, ComplianceSenderType Sender);
