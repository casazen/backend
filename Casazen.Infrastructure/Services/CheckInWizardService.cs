using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// <para>Composes, never re-implements: the completeness of the record is <see cref="AlloggiatiRecordRules"/> (through
/// <see cref="CheckInReadinessRules"/>), the codes <see cref="IAlloggiatiCodeTableService"/>, the tax <see cref="IStayTouristTaxService"/>, the
/// channel manager <see cref="ICheckInOctorateSyncService"/>, the sender <see cref="IPropertySenderStore"/> and the arrival rules
/// <see cref="StayLifecycleRules"/>. No region requires a statistical field in this release (the regional ISTAT service, E03/F01/US08, is
/// deferred) and CasaZen does not send to the police itself (E03/F01/US06 deferred).</para>
/// <para>The guests are saved like the host entry of the Alloggiati page: complete data schedules the communication for the arrival day
/// (idempotent; a failure is logged and the guests stay saved). Logs carry ids and counts, never a name or a document.</para>
/// </remarks>
public sealed class CheckInWizardService(
    AppDbContext db,
    IStayGuestService stayGuests,
    IAlloggiatiCodeTableService codeTables,
    IStayTouristTaxService stayTouristTax,
    ICheckInOctorateSyncService octorate,
    IPropertySenderStore senders,
    IAlloggiatiWebService alloggiati,
    IAlloggiatiReportScheduler alloggiatiScheduler,
    IFeatureFlags featureFlags,
    IOptions<OctorateOptions> octorateOptions,
    ILogger<CheckInWizardService> logger,
    TimeProvider? timeProvider = null) : ICheckInWizardService
{
    /// <summary>The statistical fields a region requires: none until the regional ISTAT service exists (E03/F01/US08).</summary>
    private static readonly IReadOnlyList<string> NoRequiredGuestFields = [];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<CheckInState> GetStateAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        return await BuildStateAsync(booking, cancellationToken);
    }

    public async Task<CheckInSaveResult> SaveGuestsAsync(
        Guid bookingId,
        IReadOnlyList<StayGuestInput> guests,
        string userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guests);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var booking = await LoadBookingAsync(bookingId, cancellationToken);

        var result = await stayGuests.ReplaceAsync(booking, guests, StayGuestAuthor.Host(userId), cancellationToken: cancellationToken);
        if (!result.Success)
            return new CheckInSaveResult(result.Errors, null);

        logger.LogInformation(
            "Guests of booking {BookingId} saved in the check-in wizard by {UserId}: {GuestCount} guests",
            booking.Id, userId, result.Guests.Count);
        if (booking.NoShowAt is null && AlloggiatiRecordRules.IsDataComplete(result.Guests))
            await ScheduleAlloggiatiAsync(booking.Id);

        return new CheckInSaveResult([], await BuildStateAsync(booking, cancellationToken));
    }

    public async Task<CheckInConfirmation> ConfirmAsync(Guid bookingId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        var state = await BuildStateAsync(booking, cancellationToken);

        var blocking = state.Issues.Where(i => i.Blocking).ToList();
        if (blocking.Count > 0)
        {
            throw new DomainRuleException(CheckInWizardCodes.NotReady, "CheckInNotReady")
            {
                Extensions = new Dictionary<string, object?> { [CheckInWizardCodes.IssuesExtension] = blocking },
            };
        }

        var tax = await ConfirmTaxAsync(booking.Id, state, userId, cancellationToken);
        var sent = await octorate.SendGuestsAsync(booking.Id, cancellationToken);
        var next = AlloggiatiNext(state.Sender, state.Alloggiati);
        logger.LogInformation(
            "Check-in of booking {BookingId} confirmed by {UserId}: tax saved {TaxSaved} ({TaxReason}), Octorate {OctorateState} ({OctorateReason}), police {AlloggiatiNext}",
            booking.Id, userId, tax.Saved, tax.Reason, sent.State, sent.Reason, next.Step);

        return new CheckInConfirmation(tax, sent, next, await BuildStateAsync(booking, cancellationToken));
    }

    /// <summary>The first step with something to do: the guests, their documents, the other checks, the tax, the send to Octorate, the result.</summary>
    internal static string SuggestedStep(
        int savedGuests,
        IReadOnlyList<CheckInIssue> issues,
        StayTouristTaxView? tax,
        bool octorateReceivesGuests,
        OctorateCheckInView octorateView,
        StayAttendance attendance)
    {
        var blocking = issues.Where(i => i.Blocking).ToList();
        if (savedGuests == 0
            || blocking.Any(i => (i.Code is CheckInIssueCodes.GuestFieldMissing or CheckInIssueCodes.GuestFieldRequired) && !IsDocumentField(i.Field)))
        {
            return CheckInWizardSteps.Guests;
        }

        if (blocking.Any(i => IsDocumentField(i.Field)))
            return CheckInWizardSteps.Documents;
        if (blocking.Count > 0)
            return CheckInWizardSteps.Checks;

        // A comune with no rate is shown in the tax step but never forces it: the host may have nothing to do there.
        if (tax is { CollectorNeeded: true }
            || tax is { Status: TouristTaxQuoteStatus.Calculated, Total: not null } && (tax.ConfirmedAt is null || tax.Stale))
        {
            return CheckInWizardSteps.Tax;
        }

        if (octorateReceivesGuests
            && attendance is not (StayAttendance.NoShow or StayAttendance.Inactive)
            && (octorateView.Guests.Count < savedGuests || octorateView.Guests.Any(g => g.Status != OctorateSyncStatus.Sent)))
        {
            return CheckInWizardSteps.Send;
        }

        return CheckInWizardSteps.Result;
    }

    private static bool IsDocumentField(string? field) => field is not null && CheckInReadinessRules.DocumentFields.Contains(field);

    private async Task<CheckInState> BuildStateAsync(Booking booking, CancellationToken cancellationToken)
    {
        var guests = await stayGuests.GetForBookingAsync(booking, cancellationToken);
        var saved = guests.Where(g => g.Id != Guid.Empty).ToList();
        var codeBook = await codeTables.LoadCodeBookAsync(saved, cancellationToken);
        var codes = saved.Select(codeBook.ResolveGuest).ToList();

        var sender = await senders.GetAsync(booking.PropertyId, cancellationToken);
        var octorateView = await octorate.GetStatusAsync(booking.Id, cancellationToken);
        var options = octorateOptions.Value;
        var octorateReceivesGuests = octorateView.Mirrored
            && sender.Applies
            && (sender.AlloggiatiSender == ComplianceSenderType.ChannelManager || options.SendGuestsWhenSenderIsNotChannelManager);

        var checkIn = DateOnly.FromDateTime(booking.CheckInDate);
        var checkOut = DateOnly.FromDateTime(booking.CheckOutDate);
        var issues = CheckInReadinessRules.Evaluate(new CheckInReadinessInput(
            saved, codes, checkIn, booking.NumberOfGuests, octorateReceivesGuests, NoRequiredGuestFields));

        StayTouristTaxView? tax = null;
        try
        {
            tax = await stayTouristTax.GetAsync(booking.Id, cancellationToken);
        }
        catch (DomainException ex)
        {
            // The tax step says what is missing; the rest of the wizard works without it.
            logger.LogWarning("Tourist tax of booking {BookingId} not available in the check-in wizard: {Code}", booking.Id, ex.Code);
        }

        var alloggiatiStatus = await alloggiati.GetStatusAsync(booking.Id);
        var today = _clock.TodayInRome();
        var alloggiatiSent = await StayLifecycleService.IsAlloggiatiSentAsync(db, booking.Id, cancellationToken);
        var capabilities = new CheckInCapabilities(
            DocumentReading: featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification),
            CanRegisterArrival: StayLifecycleRules.ArrivalError(booking, today) is null,
            CanMarkNoShow: booking.NoShowAt is null && CheckInReadinessRules.NoShowError(booking, today, alloggiatiSent) is null,
            OctorateTaxValues: options.WriteCityTaxValues && octorateReceivesGuests);
        var attendance = CheckInAttendance.Of(booking);

        return new CheckInState(
            new CheckInBookingView(
                booking.Id,
                booking.PropertyId,
                booking.Property.Name,
                checkIn,
                checkOut,
                checkOut.DayNumber - checkIn.DayNumber,
                booking.Source,
                booking.NumberOfGuests,
                booking.Status,
                booking.NoShowAt),
            attendance,
            guests,
            NoRequiredGuestFields,
            issues,
            tax,
            sender,
            octorateView,
            alloggiatiStatus,
            capabilities,
            SuggestedStep(saved.Count, issues, tax, octorateReceivesGuests, octorateView, attendance));
    }

    /// <summary>
    /// The tax of the stay is confirmed only when it is calculable: a comune with no rate, or a stay that will not happen, never blocks the
    /// check-in and is reported with its reason.
    /// </summary>
    private async Task<CheckInTaxOutcome> ConfirmTaxAsync(Guid bookingId, CheckInState state, string userId, CancellationToken cancellationToken)
    {
        if (state.Attendance is StayAttendance.NoShow or StayAttendance.Inactive)
            return new CheckInTaxOutcome(false, CheckInOctorateSyncService.ReasonBookingNotActive);
        if (state.Tax is not { Status: TouristTaxQuoteStatus.Calculated, Total: not null })
            return new CheckInTaxOutcome(false, StayTouristTaxService.NotCalculableCode);

        try
        {
            await stayTouristTax.ConfirmAsync(bookingId, userId, cancellationToken);
            return new CheckInTaxOutcome(true, null);
        }
        catch (DomainException ex)
        {
            logger.LogWarning("Tourist tax of booking {BookingId} not confirmed by the check-in wizard: {Code}", bookingId, ex.Code);
            return new CheckInTaxOutcome(false, ex.Code);
        }
    }

    /// <summary>
    /// What the police communication does next (the scope of this release): the channel manager sends, or the host sends on the portal with the
    /// guide; nothing when it was already sent or the property is outside Italy.
    /// </summary>
    private static CheckInAlloggiatiNext AlloggiatiNext(PropertySenderInfo sender, AlloggiatiStatusInfo status)
    {
        if (!sender.Applies)
            return new CheckInAlloggiatiNext(CheckInAlloggiatiNextStep.NotApplicable, sender.AlloggiatiSender);
        if (AlloggiatiStatusRules.IsSent(status.Status))
            return new CheckInAlloggiatiNext(CheckInAlloggiatiNextStep.AlreadySent, sender.AlloggiatiSender);

        return sender.AlloggiatiSender == ComplianceSenderType.ChannelManager
            ? new CheckInAlloggiatiNext(CheckInAlloggiatiNextStep.ChannelManagerSends, sender.AlloggiatiSender)
            : new CheckInAlloggiatiNext(CheckInAlloggiatiNextStep.Manual, sender.AlloggiatiSender);
    }

    /// <summary>Idempotent per booking; the guests are saved: a failure is logged, and from the arrival day the booking shows "to send" anyway.</summary>
    private async Task ScheduleAlloggiatiAsync(Guid bookingId)
    {
        try
        {
            await alloggiatiScheduler.EnsureScheduledAsync(bookingId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Alloggiati report of booking {BookingId} could not be scheduled after the check-in wizard", bookingId);
        }
    }

    /// <summary>The booking of the caller's org (the tenant filter): another org's is not found.</summary>
    private async Task<Booking> LoadBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await db.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
        ?? throw new NotFoundException($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };
}
