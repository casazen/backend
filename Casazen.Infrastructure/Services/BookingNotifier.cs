using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Emails of the booking status (BK-10, A3-11, #58), rendered from <see cref="EmailTemplates"/> and queued on Hangfire
/// (<see cref="IEmailQueue"/>): confirmation to the guest and new booking to the host, cancellation to the guest, and the
/// deferred charge of "Paga più tardi" (BK-08: failed charge with the link to pay, automatic cancellation); with the new
/// booking email, the push to the host's devices (MO-04, <see cref="IPushNotificationService"/>). Called
/// once the change is committed, by the code that made the transition (payment webhook, saved card, host acceptance,
/// host cancellation), so a duplicate webhook that changes nothing sends nothing. A failure is logged with the booking
/// id only and never undoes the change.
/// </summary>
/// <remarks>
/// Recipients have no language preference and the booking does not record the checkout language: the emails go out in
/// Italian (<see cref="EmailTemplates.DefaultCulture"/>), the English texts are ready. The emails of the "pay at the
/// property" requests are <see cref="OnSiteRequestNotifier"/>'s, the refund confirmations
/// <see cref="PaymentRefundService"/>'s.
/// <para><b>The online check-in in the confirmation (E23/F02/US01/T01, NF-122, NF-63).</b> The checkout tells the guest that the link of the online check-in arrives at their address,
/// so the confirmation says where it stands, by the rule of the guest page (<see cref="GuestCheckInAccessRules"/>: the same Europe/Rome calendar day and the same
/// <c>CheckIn:SendWindowDays</c> as <c>GuestCheckInSendJob</c>). The check-in is open: the link is issued as the job issues it (<see cref="IGuestCheckInService.IssueLinkAsync"/>: one
/// session, the lifetime of <c>CheckIn:SessionLifetimeDays</c>) and the e-mail carries it; the confirmation is the delivery, so no second e-mail is queued and the next run of the job finds a
/// usable link and sends nothing. The link is «in attesa» until the provider accepts the e-mail: the session says <c>Queued</c> and the delivery row <c>Sending</c>
/// (<see cref="ICheckInLinkDeliveryService.RecordEmailQueuedAsync"/>, written before the e-mail is queued), and <see cref="BookingConfirmationEmailJob"/> turns them into <c>Sent</c> or
/// <c>Failed</c> (CO-09, A5-26: never «sent» when nothing left). Not open yet: the e-mail says the day the job sends the link. Not needed, or done: it says nothing. A link that cannot be
/// issued leaves the confirmation as it is, with the day of the next run of the job, which retries as it does today; a confirmation that cannot be queued, or that the provider never takes,
/// takes the link back (it is <c>Failed</c> and its session expires), so the job sends one. The host is told of the new booking before any of this is settled, whatever happens to the request.
/// No token, address or other personal data in a log.</para>
/// </remarks>
public sealed class BookingNotifier(
    AppDbContext db,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IPushNotificationService pushNotifications,
    ILogger<BookingNotifier> logger,
    IGuestCheckInService? checkIn = null,
    ICheckInLinkDeliveryService? linkDeliveries = null,
    IOptions<GuestCheckInOptions>? checkInOptions = null,
    TimeProvider? timeProvider = null,
    IBookingFlowSettingsService? flowSettings = null)
{
    /// <summary>
    /// The four optional services of the check-in link were given (the check-in, the delivery, its options, the clock): the container gives them. Built without them the notifier says
    /// nothing about the check-in and issues no link, in silence, so a test over the real container reads this (E23/F02/US01/T01).
    /// </summary>
    internal bool IssuesCheckInLinks => checkIn is not null && linkDeliveries is not null && checkInOptions is not null && timeProvider is not null;

    /// <summary>
    /// The booking has just become confirmed (<paramref name="kind"/>): confirmation to the guest and, unless the host
    /// confirmed it (<see cref="BookingConfirmationKind.OnSite"/>), the new booking to the host. Skipped when the booking
    /// is no longer confirmed (cancelled in the meantime).
    /// </summary>
    public async Task BookingConfirmedAsync(Guid bookingId, BookingConfirmationKind kind, CancellationToken cancellationToken = default)
    {
        BookingEmailData? data;
        try
        {
            data = await LoadAsync(bookingId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Confirmation emails of booking {BookingId} could not be prepared", bookingId);
            return;
        }

        if (data is null)
        {
            logger.LogWarning("Confirmation emails of booking {BookingId} skipped: booking not found", bookingId);
            return;
        }

        if (data.Status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut))
        {
            logger.LogWarning(
                "Confirmation emails of booking {BookingId} skipped: the booking is {Status} by now",
                bookingId,
                data.Status);
            return;
        }

        // E23/F02/US01/T01: what the confirmation says about the online check-in, with the link when the check-in is already open. Never throws.
        var confirmationCheckIn = await PrepareCheckInAsync(data, cancellationToken);

        // A link that is about to leave is «in attesa» (queued, never sent) until the provider accepts the e-mail that carries it: written BEFORE the e-mail is queued, so that the job that
        // sends it cannot finish first.
        await RecordCheckInQueuedAsync(data, confirmationCheckIn);

        var queued = Queue(bookingId, EmailTemplates.Names.GuestBookingConfirmed, data.GuestEmail, () =>
        {
            var content = EmailTemplates.GuestBookingConfirmed(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.Summary,
                kind,
                data.PaidAmount,
                data.FreeRefundDeadline,
                data.HostContact,
                links.GuestBookings(data.OrgSlug, data.Summary.BookingCode),
                confirmationCheckIn?.Notice);

            // The job that sends an e-mail with a link writes on the session whether the link really left.
            return confirmationCheckIn?.Link is { } issued
                ? content with
                {
                    CheckInLink = new CheckInLinkEmail(data.BookingId, issued.SessionId),
                }
                : content;
        });

        // The host is told of the new booking (e-mail, push, bell) the moment the e-mail of the guest is with the queue, BEFORE anything of the link is settled: no await with the token of
        // the request sits between the two, so a request that is cancelled right after the confirmation was queued cannot make the host lose the alert.
        if (kind != BookingConfirmationKind.OnSite)
            AlertHostOfNewBooking(data, kind);

        await TakeBackCheckInAsync(data, confirmationCheckIn, queued);
    }

    /// <summary>
    /// What the confirmation of <paramref name="data"/> says about the online check-in, by the rule of the guest page: the check-in is open, so the link is issued and the e-mail carries
    /// it (<see cref="ConfirmationCheckIn.Link"/>); not open yet, so the e-mail says the day the daily job sends the link; completed or not needed, so <c>null</c> (nothing is said). Also
    /// <c>null</c> without the services of the check-in, without a public site to build a link on, for a guest with no address to write to, or when the state of the check-in cannot be
    /// read (logged): the e-mail is then what it always was and the job does what it always does. Never throws.
    /// </summary>
    private async Task<ConfirmationCheckIn?> PrepareCheckInAsync(BookingEmailData data, CancellationToken cancellationToken)
    {
        // A link is not issued for an e-mail that cannot carry it: no public site to build it on, no address to send it to (an issued link, however unsent, stops the job from sending one).
        if (checkIn is null || linkDeliveries is null || !links.IsConfigured || string.IsNullOrWhiteSpace(data.GuestEmail))
            return null;

        try
        {
            var clock = timeProvider ?? TimeProvider.System;
            var today = clock.TodayInRomeAsDateOnly();
            var windowDays = (checkInOptions?.Value ?? new GuestCheckInOptions()).SendWindowDays;
            if (flowSettings is not null)
            {
                // casazen/backend#978: the org's booking flow. The host sends the links (manual): the confirmation issues none and says nothing about it.
                var flow = await flowSettings.GetAsync(data.OrgId, cancellationToken);
                if (!flow.SendsCheckInLink)
                    return null;
                windowDays = flow.CheckInLinkDaysBefore;
            }

            var access = await GuestCheckInAccessRules.ReadAsync(
                db,
                data.BookingId,
                data.Status,
                data.Summary.CheckInDate,
                data.Summary.CheckOutDate,
                today,
                clock.GetUtcNow().UtcDateTime,
                windowDays,
                cancellationToken);

            switch (access.Status)
            {
                case GuestCheckInAccessStatus.NotYetOpen when access.OpensOn is { } opensOn:
                    return new ConfirmationCheckIn(BookingCheckInNotice.OnDay(DayOf(opensOn)), null);
                case GuestCheckInAccessStatus.Open:
                    return await IssueLinkAsync(data, today);
                default:
                    // Completed (the data or the communication were sent) or not applicable: nothing to say about a link.
                    return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The state of the online check-in of booking {BookingId} could not be read for its confirmation", data.BookingId);
            return null;
        }
    }

    /// <summary>
    /// The check-in is open: issues the link the way the daily job does (one session, the same lifetime) and makes the notice that carries it. A link that cannot be issued is logged
    /// with the booking only, and the notice is the day of the next run of the job, which issues it as it does today.
    /// </summary>
    private async Task<ConfirmationCheckIn> IssueLinkAsync(BookingEmailData data, DateOnly today)
    {
        try
        {
            var issued = await checkIn!.IssueLinkAsync(data.BookingId, data.OrgId);
            return new ConfirmationCheckIn(BookingCheckInNotice.WithLink(links.GuestCheckIn(issued.Token), issued.ExpiresAt), issued);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The check-in link of booking {BookingId} could not be issued for its confirmation", data.BookingId);
            return new ConfirmationCheckIn(NextRunNotice(data, today), null);
        }
    }

    /// <summary>
    /// What to tell a guest whose link was not issued with the confirmation although the check-in is open: the daily job runs again the next day (Europe/Rome), so the link arrives then, as
    /// long as the job still selects the stay that day (a confirmed stay that has not started, a checked-in one that is not over); otherwise no day is promised and the link can be requested.
    /// </summary>
    private static BookingCheckInNotice NextRunNotice(BookingEmailData data, DateOnly today)
    {
        var tomorrow = today.AddDays(1);
        var stillSelected = data.Status == BookingStatus.CheckedIn
            ? RomeCalendar.DateInRome(data.Summary.CheckOutDate) >= tomorrow
            : RomeCalendar.DateInRome(data.Summary.CheckInDate) >= tomorrow;
        return stillSelected ? BookingCheckInNotice.OnDay(DayOf(tomorrow)) : BookingCheckInNotice.RequestOnly;
    }

    /// <summary>A calendar day as a date value of the emails: midnight UTC of the day (the storage convention of the dates of a stay), written without any time zone conversion.</summary>
    private static DateTime DayOf(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>
    /// Before the confirmation is handed to the queue, when it carries a link: the confirmation is the delivery, so the session says <c>Queued</c> and the delivery row <c>Sending</c> («in
    /// attesa»; the job that sends the e-mail turns them into <c>Sent</c> or <c>Failed</c> once the provider answers) and the next run of the daily job finds a usable link and sends
    /// nothing. Never throws: a failure is logged with the booking only (the link stays usable and the job that sends the e-mail still writes the outcome on the session).
    /// </summary>
    private async Task RecordCheckInQueuedAsync(BookingEmailData data, ConfirmationCheckIn? confirmationCheckIn)
    {
        if (confirmationCheckIn?.Link is not { } issued || linkDeliveries is null)
            return;

        try
        {
            await linkDeliveries.RecordEmailQueuedAsync(data.BookingId, issued, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The check-in link of booking {BookingId} could not be recorded as queued with its confirmation", data.BookingId);
        }
    }

    /// <summary>
    /// After the confirmation was handed to the queue: when it carries a link and the queue did not take it (no provider, a queue that refused it), the link is taken back, it is <c>Failed</c>
    /// and its session expires (<see cref="ICheckInLinkDeliveryService.RecordEmailFailedAsync"/>), so that the daily job issues and sends one at its next run. A confirmation that was queued
    /// is settled by <see cref="BookingConfirmationEmailJob"/>, not here. A step of the change that is already committed: it does not take the token of the request (a request that is
    /// cancelled must not leave a link behind that nobody has). Never throws: a failure is logged with the booking only.
    /// </summary>
    private async Task TakeBackCheckInAsync(BookingEmailData data, ConfirmationCheckIn? confirmationCheckIn, bool queued)
    {
        if (queued || confirmationCheckIn?.Link is not { } issued || linkDeliveries is null)
            return;

        try
        {
            await linkDeliveries.RecordEmailFailedAsync(data.BookingId, issued.SessionId, GuestCheckInLinkEmailErrors.QueueFailed, CancellationToken.None);
            logger.LogWarning("Check-in link of booking {BookingId} taken back: its confirmation was not queued", data.BookingId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The check-in link of booking {BookingId} could not be taken back after its confirmation was not queued", data.BookingId);
        }
    }

    /// <summary>What the confirmation says about the online check-in and, when it carries the link, the link just issued (its raw token exists only here).</summary>
    private sealed record ConfirmationCheckIn(BookingCheckInNotice Notice, IssuedCheckInLink? Link);

    /// <summary>
    /// A booking that arrived from a channel (the channel manager) is confirmed (E08/F06/US01/T02, NF-79): the push to the hosts'
    /// devices that a direct booking already sends, with the name of the channel in the title. No email to the guest (the channel
    /// writes to its guest) and none to the host (the push is the alert). The recipients and the push key are the ones of
    /// <see cref="AlertHostOfNewBooking"/>, the key being the booking: a redelivered event, or the creation and the confirmation of the
    /// same booking, are one push. Skipped when the booking is no longer confirmed (cancelled in the meantime).
    /// </summary>
    public async Task ChannelBookingAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var data = await TryLoadAsync(bookingId, "Channel booking", cancellationToken);
        if (data is null)
            return;

        if (data.Status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut))
        {
            logger.LogWarning(
                "Channel booking alert of booking {BookingId} skipped: the booking is {Status} by now",
                bookingId,
                data.Status);
            return;
        }

        try
        {
            // No guest name on the lock screen: property, channel, dates and number of guests.
            var push = EmailTemplates.ChannelBookingPush(
                EmailTemplates.DefaultCulture,
                data.Summary.PropertyName,
                data.ChannelName,
                data.Summary.CheckInDate,
                data.Summary.CheckOutDate,
                data.Summary.Guests);
            if (!EnqueueNewBookingPush(data, push))
                logger.LogWarning("Channel booking push for booking {BookingId} was not queued", data.BookingId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Channel booking push for booking {BookingId} could not be prepared", data.BookingId);
        }
    }

    /// <summary>
    /// The host cancelled the booking: one email to the guest with the refund Stripe already confirmed
    /// (<paramref name="refundedAmount"/>, whose own confirmation email the caller has claimed) and the refund still in
    /// progress (<paramref name="refundStartedAmount"/>, confirmed by a later email). The host's reason is never sent.
    /// </summary>
    public async Task BookingCancelledByHostAsync(
        Guid bookingId,
        decimal refundedAmount,
        decimal refundStartedAmount,
        CancellationToken cancellationToken = default)
    {
        BookingEmailData? data;
        try
        {
            data = await LoadAsync(bookingId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Cancellation email of booking {BookingId} could not be prepared", bookingId);
            return;
        }

        if (data is null)
        {
            logger.LogWarning("Cancellation email of booking {BookingId} skipped: booking not found", bookingId);
            return;
        }

        Queue(bookingId, EmailTemplates.Names.GuestBookingCancelled, data.GuestEmail, () =>
            EmailTemplates.GuestBookingCancelled(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.Summary.PropertyName,
                data.Summary.CheckInDate,
                data.Summary.CheckOutDate,
                refundedAmount,
                refundStartedAmount,
                data.HostContact,
                currency: data.Summary.Currency));
    }

    /// <summary>
    /// The guest cancelled the booking themselves, from the guest area or with the host's link (DB-08a): an email to the guest that
    /// says so, with the refund Stripe already confirmed (<paramref name="refundedAmount"/>, whose own confirmation email the caller has
    /// claimed), the refund still in progress (<paramref name="refundStartedAmount"/>, confirmed by a later email) and what the policy
    /// keeps (<paramref name="retainedAmount"/>); and one to the host, who did not do it, with the same figures and the link to the
    /// booking. Nothing of the guest's session or link is in them.
    /// </summary>
    public async Task BookingCancelledByGuestAsync(
        Guid bookingId,
        decimal refundedAmount,
        decimal refundStartedAmount,
        decimal retainedAmount,
        CancellationToken cancellationToken = default)
    {
        var data = await TryLoadAsync(bookingId, "Guest cancellation", cancellationToken);
        if (data is null)
            return;

        Queue(bookingId, EmailTemplates.Names.GuestBookingCancelled, data.GuestEmail, () =>
            EmailTemplates.GuestBookingCancelled(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.Summary.PropertyName,
                data.Summary.CheckInDate,
                data.Summary.CheckOutDate,
                refundedAmount,
                refundStartedAmount,
                data.HostContact,
                BookingCancellationEmailCause.Guest,
                retainedAmount,
                data.Summary.Currency));
        QueueToHost(data, EmailTemplates.Names.HostBookingCancelledByGuest, () =>
            EmailTemplates.HostBookingCancelledByGuest(
                EmailTemplates.DefaultCulture,
                $"{data.GuestFirstName} {data.GuestLastName}".Trim(),
                data.Summary,
                refundedAmount + refundStartedAmount,
                retainedAmount,
                links.HostBooking(data.BookingId)));
    }

    /// <summary>
    /// The host changed the dates or the guests of a confirmed booking of the booking site (E08/F02/US01/T01, NF-75): one email to the guest with the
    /// stay as it is now, the new total and what happens to the money (<paramref name="settlement"/>: a refund, a charge of the saved card, or a payment
    /// link that follows in its own email). The notes of the host are never in it. Called once the edit is committed: a failure is logged with the
    /// booking id only and never undoes it.
    /// </summary>
    public async Task BookingModifiedAsync(Guid bookingId, BookingEditSettlement settlement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        var data = await TryLoadAsync(bookingId, "Booking change", cancellationToken);
        if (data is null)
            return;

        // A booking cancelled in the meantime has nothing to tell.
        if (data.Status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut))
            return;

        Queue(bookingId, EmailTemplates.Names.GuestBookingModified, data.GuestEmail, () =>
            EmailTemplates.GuestBookingModified(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.Summary,
                settlement.Action == BookingSettlementAction.Refund ? settlement.Amount : 0m,
                settlement.Action == BookingSettlementAction.ChargeSavedCard ? settlement.Amount : 0m,
                settlement.Action == BookingSettlementAction.PaymentLink,
                data.HostContact));
    }

    /// <summary>
    /// The deferred charge failed and needs the guest (BK-08, A3-14): the guest gets the link to pay on the checkout outcome
    /// page (<paramref name="checkoutToken"/>, the raw token whose hash was just stored), the host is told. The last day to
    /// pay is the day before <paramref name="cancellationDay"/> when an automatic cancellation applies.
    /// </summary>
    public async Task DeferredChargeFailedAsync(
        Guid bookingId,
        string checkoutToken,
        DateOnly? cancellationDay,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutToken);
        var data = await TryLoadAsync(bookingId, "Deferred charge failure", cancellationToken);
        if (data is null)
            return;

        var cancelOn = cancellationDay?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        Queue(bookingId, EmailTemplates.Names.GuestDeferredChargeFailed, data.GuestEmail, () =>
            EmailTemplates.GuestDeferredChargeFailed(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.Summary,
                links.CheckoutOutcome(data.OrgSlug, data.BookingId, checkoutToken),
                cancelOn?.AddDays(-1),
                data.HostContact));
        AlertHostOfFailedDeferredCharge(data, guestAsked: true, cancelOn);
    }

    /// <summary>
    /// Every attempt of the deferred charge ended without a payment the guest could complete (Stripe unreachable, no saved
    /// payment method): the host only, who handles the payment with the guest (BK-08).
    /// </summary>
    public async Task DeferredChargeNotAttemptedAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var data = await TryLoadAsync(bookingId, "Deferred charge failure", cancellationToken);
        if (data is not null)
            AlertHostOfFailedDeferredCharge(data, guestAsked: false, cancelOnDay: null);
    }

    /// <summary>
    /// "Paga più tardi" not paid in time: the system cancelled the booking and released its dates (BK-08). The guest gets
    /// the standard cancellation email with its own cause (nothing charged), the host a notice.
    /// </summary>
    public async Task DeferredChargeCancelledAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var data = await TryLoadAsync(bookingId, "Cancellation", cancellationToken);
        if (data is null)
            return;

        Queue(bookingId, EmailTemplates.Names.GuestBookingCancelled, data.GuestEmail, () =>
            EmailTemplates.GuestBookingCancelled(
                EmailTemplates.DefaultCulture,
                data.GuestFirstName,
                data.Summary.PropertyName,
                data.Summary.CheckInDate,
                data.Summary.CheckOutDate,
                refundedEur: 0m,
                refundStartedEur: 0m,
                data.HostContact,
                BookingCancellationEmailCause.DeferredPaymentNotCompleted,
                currency: data.Summary.Currency));
        QueueToHost(data, EmailTemplates.Names.HostDeferredChargeCancelled, () =>
            EmailTemplates.HostDeferredChargeCancelled(
                EmailTemplates.DefaultCulture,
                $"{data.GuestFirstName} {data.GuestLastName}".Trim(),
                data.Summary,
                links.HostBooking(data.BookingId)));
    }

    private void AlertHostOfFailedDeferredCharge(BookingEmailData data, bool guestAsked, DateTime? cancelOnDay) =>
        QueueToHost(data, EmailTemplates.Names.HostDeferredChargeFailed, () =>
            EmailTemplates.HostDeferredChargeFailed(
                EmailTemplates.DefaultCulture,
                $"{data.GuestFirstName} {data.GuestLastName}".Trim(),
                data.Summary,
                guestAsked,
                cancelOnDay,
                links.HostBooking(data.BookingId)));

    private async Task<BookingEmailData?> TryLoadAsync(Guid bookingId, string what, CancellationToken cancellationToken)
    {
        try
        {
            var data = await LoadAsync(bookingId, cancellationToken);
            if (data is null)
                logger.LogWarning("{What} emails of booking {BookingId} skipped: booking not found", what, bookingId);
            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{What} emails of booking {BookingId} could not be prepared", what, bookingId);
            return null;
        }
    }

    /// <summary>
    /// The only place where the host learns of a new booking confirmed without their action: an email to
    /// <c>Org.ContactEmail</c> and a push to the hosts' devices (MO-04, A6-08), both queued on Hangfire. The caller runs
    /// once per confirmation, and the push key is the booking, so a duplicate webhook or a retried job sends no second push.
    /// </summary>
    private void AlertHostOfNewBooking(BookingEmailData data, BookingConfirmationKind kind)
    {
        QueueToHost(data, EmailTemplates.Names.HostBookingConfirmed, () =>
            EmailTemplates.HostBookingConfirmed(
                EmailTemplates.DefaultCulture,
                $"{data.GuestFirstName} {data.GuestLastName}".Trim(),
                data.Summary,
                kind,
                data.PaidAmount,
                data.FreeRefundDeadline,
                links.HostBooking(data.BookingId)));

        try
        {
            // No guest name on the lock screen: property, dates and number of guests.
            var push = EmailTemplates.NewBookingPush(
                EmailTemplates.DefaultCulture,
                data.Summary.PropertyName,
                data.Summary.CheckInDate,
                data.Summary.CheckOutDate,
                data.Summary.Guests);
            EnqueueNewBookingPush(data, push);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "New booking push for booking {BookingId} could not be prepared", data.BookingId);
        }
    }

    /// <summary>
    /// The new-booking push to the hosts of the booking, whichever way the booking came (a direct checkout, a channel): the key is the
    /// booking, so a device gets one push per booking, and the audience is its hosts (until the per-user preferences of NF-65 decide).
    /// </summary>
    private bool EnqueueNewBookingPush(BookingEmailData data, PushText push) =>
        pushNotifications.Enqueue(
            PushDeliveryKeys.NewBooking(data.BookingId),
            PushAudience.BookingHosts(data.BookingId),
            new PushNotificationPayload(push.Title, push.Body, PushTypes.NewBooking, data.BookingId, PushRoutes.Booking(data.BookingId)));

    /// <summary>
    /// The email to the host side (AM-03): the org's contact address and, if they are other people, the member in charge of
    /// the property and the org's administrators (<see cref="HostNotificationAudience"/>), one message each. Nobody to tell is
    /// the same as before: a warning that it was not queued.
    /// </summary>
    private void QueueToHost(BookingEmailData data, string template, Func<EmailContent> render)
    {
        if (data.HostEmails.Count == 0)
        {
            Queue(data.BookingId, template, to: null, render);
            return;
        }

        foreach (var to in data.HostEmails)
            Queue(data.BookingId, template, to, render);
    }

    /// <summary>Renders the email and hands it to the queue; true when the queue took it. Never throws.</summary>
    private bool Queue(Guid bookingId, string template, string? to, Func<EmailContent> render)
    {
        try
        {
            if (emailQueue.Enqueue(to, render(), template))
                return true;

            logger.LogWarning("Email {Template} for booking {BookingId} was not queued", template, bookingId);
        }
        catch (Exception ex)
        {
            // E.g. App:PublicSiteBaseUrl missing (Development/Testing only): the booking change stays.
            logger.LogError(ex, "Email {Template} for booking {BookingId} could not be prepared", template, bookingId);
        }

        return false;
    }

    /// <summary>
    /// The booking with what its emails show. Read by id: the payment webhook and the jobs have no tenant, and a host
    /// request is already limited to the host's org by the tenant filter.
    /// </summary>
    private async Task<BookingEmailData?> LoadAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var row = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new
            {
                b.Id,
                b.BookingCode,
                b.Status,
                b.Source,
                b.ChannelLabel,
                GuestFirstName = b.Guest.FirstName,
                GuestLastName = b.Guest.LastName,
                GuestEmail = b.Guest.Email,
                PropertyName = b.Property.Name,
                PropertyCurrency = b.Property.Currency,
                OrgSlug = b.Org.Slug,
                OrgDisplayName = b.Org.DisplayName,
                OrgContactEmail = b.Org.ContactEmail,
                b.OrgId,
                b.PropertyId,
                PropertyCreatorId = b.Property.OwnerId,
                b.Property.ResponsibleUserId,
                b.CheckInDate,
                b.CheckOutDate,
                b.NumberOfGuests,
                b.BasePrice,
                b.CleaningFee,
                b.TouristTax,
                b.TotalPrice,
                b.FreeRefundDeadline,
                PaidAmount = b.Payments.Where(p => p.Status == PaymentStatus.Completed).Sum(p => p.Amount),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        var summary = new BookingEmailSummary(
            BookingCodes.Format(row.BookingCode),
            row.PropertyName,
            row.CheckInDate,
            row.CheckOutDate,
            row.NumberOfGuests,
            row.BasePrice - row.CleaningFee,
            row.CleaningFee,
            row.TouristTax,
            row.TotalPrice,
            // The amounts of the emails are in the currency of the property (CHF-MONEY).
            StayCurrency.Normalize(row.PropertyCurrency));

        // AM-03: the org's contact address, as ever, plus the member in charge of the property and the org's administrators.
        var tellEmails = await HostNotificationAudience
            .UsersToTell(db, row.OrgId, row.PropertyId, row.ResponsibleUserId, row.PropertyCreatorId)
            .Select(u => u.Email)
            .ToListAsync(cancellationToken);
        var hostEmails = new List<string>();
        foreach (var email in tellEmails.Prepend(row.OrgContactEmail))
        {
            if (!string.IsNullOrWhiteSpace(email) && !hostEmails.Contains(email.Trim(), StringComparer.OrdinalIgnoreCase))
                hostEmails.Add(email.Trim());
        }

        return new BookingEmailData(
            row.Id,
            row.OrgId,
            row.Status,
            row.GuestFirstName,
            row.GuestLastName,
            row.GuestEmail,
            row.OrgSlug,
            hostEmails,
            new BookingHostContact(row.OrgDisplayName, row.OrgContactEmail),
            summary,
            row.PaidAmount,
            row.FreeRefundDeadline,
            EmailTemplates.OtaChannelName(row.Source, row.ChannelLabel));
    }

    private sealed record BookingEmailData(
        Guid BookingId,
        Guid OrgId,
        BookingStatus Status,
        string GuestFirstName,
        string GuestLastName,
        string GuestEmail,
        string OrgSlug,
        IReadOnlyList<string> HostEmails,
        BookingHostContact HostContact,
        BookingEmailSummary Summary,
        decimal PaidAmount,
        DateTime? FreeRefundDeadline,
        string ChannelName);
}
