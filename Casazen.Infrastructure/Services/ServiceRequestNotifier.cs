using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Everything the service requests tell the two parties by email and push (SU-09, SU-10, A4-20, A6-08, A6-29; SP-04): the
/// host hears of what the supplier does (taken, started, completed, rejected, cancelled, another time proposed), the supplier
/// of what the host does (new request, reminder, cancellation, answer to a proposal, payment) and of the cancellation CasaZen
/// makes when nobody answers in time. Emails go through <see cref="IEmailQueue"/> (Hangfire) and pushes through
/// <see cref="IPushNotificationService"/>: nothing is sent inside the request of the caller.
/// </summary>
/// <remarks>
/// <para>Called only <b>after</b> the change is saved, and only by the winner of a transition (<c>xmin</c>, SU-10): the loser of
/// a race notifies nobody. A failure here is logged and never turned into an error for the caller, since the change is
/// already saved (A4-20).</para>
/// <para><b>Privacy (decision D9).</b> What goes to the supplier names the <b>comune</b> of the property, never its name nor the
/// host's notes: before the take the supplier does not know them, and a request cancelled before the take must not give them
/// away. What goes to the host names the property, which is the host's own. Pushes carry category and place only, never a
/// guest name.</para>
/// <para>A status with no email or push to the party (a request the host itself cancelled, a paid one, an unknown one) sends
/// nothing and logs nothing: it is not an error.</para>
/// <para><b>A request from a supplier's public showcase (SP-10) has no host</b>: what would go to the host (the supplier took,
/// refused, cancelled it, proposed another time, or nobody answered in time) goes to its <b>customer</b>, by e-mail, written by
/// <see cref="ShowcaseBookingNotifier"/>, and there is no push to a host. What goes to the supplier is the same, with the comune
/// of the request itself instead of the one of a property.</para>
/// </remarks>
public sealed partial class ServiceRequestNotifier(
    AppDbContext db,
    IEmailQueue emailQueue,
    PublicSiteLinks publicSiteLinks,
    IPushNotificationService pushNotifications,
    ShowcaseBookingNotifier showcase,
    IOptions<ServiceRequestOptions> options,
    ILogger<ServiceRequestNotifier> logger)
{
    private sealed record SupplierContact(string Email, string LegalName);

    public EmailContent RenderCreatedEmail(ServiceRequest request, string supplierName, string comune) =>
        EmailTemplates.ServiceRequestCreated(
            EmailTemplates.DefaultCulture,
            supplierName,
            request.Category,
            comune,
            publicSiteLinks.SupplierInbox(),
            request.ScheduledStartUtc,
            request.EstimatedAmountCents);

    /// <summary>
    /// Queues the email rendered by <see cref="RenderCreatedEmail"/> and the push of the new request, unless the supplier turned off
    /// «Nuove richieste» (SP-16d, <see cref="SupplierNotificationKind.NewRequest"/>). The request is already saved: a failure is logged,
    /// never turned into an error for the host.
    /// </summary>
    public async Task QueueCreatedAsync(
        ServiceRequest request,
        string supplierEmail,
        EmailContent email,
        string comune,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await SupplierNotificationPreferences.AllowsAsync(db, request.SupplierOrgId, SupplierNotificationKind.NewRequest, cancellationToken))
                return;

            emailQueue.Enqueue(supplierEmail, email, EmailTemplates.Names.ServiceRequestCreated);
            QueueSupplierPush(
                request,
                PushDeliveryKeys.ServiceRequestCreated(request.Id),
                PushTypes.ServiceRequestCreated,
                EmailTemplates.ServiceRequestCreatedPush(EmailTemplates.DefaultCulture, request.Category, comune));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Supplier notification for new service request {Id} could not be queued", request.Id);
        }
    }

    /// <summary>
    /// A new request that was accepted automatically for a regular host (SP-04d): email and push to the supplier, <b>instead of</b> the
    /// ones of a new request (<see cref="QueueCreatedAsync"/>), since there is nothing left to answer. Like the other notifications of
    /// the supplier it names the comune, the time and the amount, never the property or the host; the address and the host's contact
    /// are in the console, as after any take. Called with the request as it is stored (with its property), after the save. It is the notice
    /// of a new request, so it follows the same switch (SP-16d, «Nuove richieste»).
    /// </summary>
    public async Task NotifySupplierAutoAcceptedAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!await SupplierNotificationPreferences.AllowsAsync(db, request.SupplierOrgId, SupplierNotificationKind.NewRequest, cancellationToken))
                return;

            var supplier = await FindSupplierAsync(request, cancellationToken);
            var comune = ComuneOf(request);
            if (supplier is not null)
            {
                var email = EmailTemplates.ServiceRequestAutoAccepted(
                    EmailTemplates.DefaultCulture,
                    supplier.LegalName,
                    request.Category,
                    comune,
                    publicSiteLinks.SupplierInbox(),
                    request.ScheduledStartUtc,
                    request.EstimatedAmountCents);
                emailQueue.Enqueue(supplier.Email, email, EmailTemplates.Names.ServiceRequestAutoAccepted);
            }

            QueueSupplierPush(
                request,
                PushDeliveryKeys.ServiceRequestAutoAccepted(request.Id),
                PushTypes.ServiceRequestAutoAccepted,
                EmailTemplates.ServiceRequestAutoAcceptedPush(EmailTemplates.DefaultCulture, request.Category, comune));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Supplier notification for auto-accepted service request {Id} could not be queued", request.Id);
        }
    }

    public async Task NotifyHostAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (request.RentalContext == ServiceRequestRentalContext.Showcase)
        {
            await showcase.NotifyCustomerOfStatusAsync(request, cancellationToken);
            return;
        }

        await QueueHostStatusEmailAsync(request, cancellationToken);
        QueueHostStatusPush(request);
    }

    private async Task QueueHostStatusEmailAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var completion = request.Status == ServiceRequestStatus.Completato
                ? new EmailTemplates.ServiceRequestCompletionEmail(
                    request.FinalAmountCents,
                    request.FinalAmountNeedsConfirmation,
                    options.Value.FinalAmountTolerancePercent,
                    request.CompletionNotes)
                : null;

            var email = EmailTemplates.ServiceRequestStatusChanged(
                EmailTemplates.DefaultCulture,
                request.Status,
                request.Category,
                PropertyNameOf(request),
                request.Status == ServiceRequestStatus.Rifiutato ? request.RejectionReason : request.CancellationReason,
                request.CancelledBy,
                completion,
                request.ScheduledStartUtc);
            if (email is null)
                return;

            var hostEmail = await db.Orgs
                .AsNoTracking()
                .Where(o => o.Id == request.OrgId)
                .Select(o => o.ContactEmail)
                .FirstOrDefaultAsync(cancellationToken);

            emailQueue.Enqueue(hostEmail, email, EmailTemplates.Names.ServiceRequestStatusChanged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Host email for service request {Id} ({Status}) could not be queued", request.Id, request.Status);
        }
    }

    private void QueueHostStatusPush(ServiceRequest request)
    {
        try
        {
            var type = PushTypes.ForServiceRequestStatus(request.Status);
            var push = EmailTemplates.ServiceRequestStatusPush(
                EmailTemplates.DefaultCulture, request.Status, request.Category, PropertyNameOf(request), request.CancelledBy);
            if (type is null || push is null || request.PropertyId is not { } propertyId)
                return;

            pushNotifications.Enqueue(
                PushDeliveryKeys.ServiceRequestStatus(request.Id, request.Status),
                PushAudience.PropertyHosts(propertyId),
                new PushNotificationPayload(push.Title, push.Body, type, request.BookingId, HostRoute(request), request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Push notification for service request {Id} ({Status}) could not be queued", request.Id, request.Status);
        }
    }

    public async Task NotifyTimeProposedAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (request is not { ProposedStartUtc: { } start, ProposedEndUtc: { } end, ProposedAt: { } proposedAt })
            return;

        if (request.RentalContext == ServiceRequestRentalContext.Showcase)
        {
            await showcase.NotifyCustomerOfProposalAsync(request, cancellationToken);
            return;
        }

        try
        {
            var hostUrl = request.BookingId is { } bookingId ? publicSiteLinks.HostBooking(bookingId) : null;
            var email = EmailTemplates.ServiceRequestTimeProposed(
                EmailTemplates.DefaultCulture, request.Category, PropertyNameOf(request), start, end, request.ProposalMessage, hostUrl);
            var hostEmail = await db.Orgs
                .AsNoTracking()
                .Where(o => o.Id == request.OrgId)
                .Select(o => o.ContactEmail)
                .FirstOrDefaultAsync(cancellationToken);
            emailQueue.Enqueue(hostEmail, email, EmailTemplates.Names.ServiceRequestTimeProposed);

            var push = EmailTemplates.ServiceRequestTimeProposedPush(EmailTemplates.DefaultCulture, request.Category, PropertyNameOf(request));
            pushNotifications.Enqueue(
                PushDeliveryKeys.ServiceRequestTimeProposed(request.Id, proposedAt),
                PushAudience.PropertyHosts(request.PropertyId ?? throw new InvalidOperationException("A host's request has a property")),
                new PushNotificationPayload(
                    push.Title, push.Body, PushTypes.ServiceRequestTimeProposed, request.BookingId, HostRoute(request), request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Notification of the time proposed for service request {Id} could not be queued", request.Id);
        }
    }

    public async Task NotifyQuoteSentAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (ServiceRequestQuoteRules.PendingOf(request) is not { } quote)
            return;

        if (request.RentalContext == ServiceRequestRentalContext.Showcase)
        {
            await showcase.NotifyCustomerOfQuoteAsync(request, quote, cancellationToken);
            return;
        }

        try
        {
            var italian = EmailTemplates.DefaultCulture;
            var supplier = await FindSupplierAsync(request, cancellationToken);
            var hostEmail = await db.Orgs
                .AsNoTracking()
                .Where(o => o.Id == request.OrgId)
                .Select(o => o.ContactEmail)
                .FirstOrDefaultAsync(cancellationToken);

            var offeredTime = quote is { ScheduledStartUtc: not null, ScheduledEndUtc: not null };
            var email = EmailTemplates.ServiceRequestQuoteReceived(
                italian,
                customerName: null,
                supplier?.LegalName ?? string.Empty,
                request.ServiceNameSnapshot ?? EmailTemplates.ServiceCategoryLabel(italian, request.Category),
                quote.ScheduledStartUtc ?? request.ScheduledStartUtc ?? request.CreatedAt,
                offeredTime ? quote.ScheduledEndUtc : null,
                quote.Lines,
                quote.TotalCents,
                quote.Note,
                answerByUtc: null,
                request.BookingId is { } bookingId ? publicSiteLinks.HostBooking(bookingId) : null);
            emailQueue.Enqueue(hostEmail, email, EmailTemplates.Names.ServiceRequestQuoteReceived);

            var push = EmailTemplates.ServiceRequestQuoteSentPush(italian, request.Category, PropertyNameOf(request));
            pushNotifications.Enqueue(
                PushDeliveryKeys.ServiceRequestQuote(request.Id, quote.SentAt, "sent"),
                PushAudience.PropertyHosts(request.PropertyId ?? throw new InvalidOperationException("A host's request has a property")),
                new PushNotificationPayload(
                    push.Title, push.Body, PushTypes.ServiceRequestQuoteSent, request.BookingId, HostRoute(request), request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Notification of the quote sent for service request {Id} could not be queued", request.Id);
        }
    }

    public async Task NotifyQuoteAnsweredAsync(ServiceRequest request, bool accepted, ServiceRequestQuote quote, CancellationToken cancellationToken)
    {
        try
        {
            var italian = EmailTemplates.DefaultCulture;
            var supplier = await FindSupplierAsync(request, cancellationToken);
            var comune = ComuneOf(request);
            if (supplier is not null)
            {
                var email = EmailTemplates.ServiceRequestQuoteAnswered(
                    italian,
                    supplier.LegalName,
                    request.ServiceNameSnapshot ?? EmailTemplates.ServiceCategoryLabel(italian, request.Category),
                    comune,
                    customerShortName: string.Empty,
                    accepted,
                    quote.TotalCents,
                    accepted ? request.ScheduledStartUtc : null,
                    publicSiteLinks.SupplierInbox());
                emailQueue.Enqueue(supplier.Email, email, EmailTemplates.Names.ServiceRequestQuoteAnswered);
            }

            QueueSupplierPush(
                request,
                PushDeliveryKeys.ServiceRequestQuote(request.Id, quote.SentAt, accepted ? "accepted" : "rejected"),
                accepted ? PushTypes.ServiceRequestQuoteAccepted : PushTypes.ServiceRequestQuoteRejected,
                EmailTemplates.ServiceRequestQuoteAnsweredPush(italian, request.Category, comune, accepted));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Answer to the quote of service request {Id} could not be queued", request.Id);
        }
    }

    /// <summary>
    /// Email and push to the supplier when the request was cancelled by the host, or by CasaZen because nobody answered in
    /// time (<see cref="ServiceRequest.CancelledBy"/> says which). The supplier is told whatever its status.
    /// <paramref name="lostReliabilityPoints"/> adds to the email of a request nobody answered that the missed answer costs the supplier
    /// points of its reliability (SP-REL), which is what puts it lower in the lists; false when it was excused (paused, time off, closed).
    /// </summary>
    public async Task NotifySupplierCancelledAsync(
        ServiceRequest request,
        CancellationToken cancellationToken,
        bool lostReliabilityPoints = false)
    {
        if (request.CancelledBy is not { } cancelledBy || cancelledBy == ServiceRequestActorParty.Supplier)
            return;

        // A showcase request that lapsed because its customer did not answer the time the supplier proposed (SP-11): the supplier did
        // answer, so it is not told it did not (decision D24). The customer's own cancellation has its own notification.
        if (request.RentalContext == ServiceRequestRentalContext.Showcase
            && cancelledBy == ServiceRequestActorParty.System
            && request.CancellationReason == ServiceRequestCancellationReasons.ProposalNotAnswered)
        {
            await showcase.NotifySupplierOfLapsedProposalAsync(request, cancellationToken);
            return;
        }

        if (request.RentalContext == ServiceRequestRentalContext.Showcase
            && cancelledBy == ServiceRequestActorParty.System
            && request.CancellationReason == ServiceRequestCancellationReasons.QuoteNotAnswered)
        {
            await showcase.NotifySupplierOfLapsedQuoteAsync(request, cancellationToken);
            return;
        }

        if (cancelledBy == ServiceRequestActorParty.Customer)
            return;

        try
        {
            var supplier = await FindSupplierAsync(request, cancellationToken);
            var comune = ComuneOf(request);
            if (supplier is not null)
            {
                var email = EmailTemplates.ServiceRequestCancelledToSupplier(
                    EmailTemplates.DefaultCulture,
                    supplier.LegalName,
                    request.Category,
                    comune,
                    cancelledBy,
                    request.CancellationReason,
                    publicSiteLinks.SupplierInbox(),
                    lostReliabilityPoints);
                emailQueue.Enqueue(supplier.Email, email, EmailTemplates.Names.ServiceRequestCancelledToSupplier);
            }

            QueueSupplierPush(
                request,
                PushDeliveryKeys.ServiceRequestStatus(request.Id, ServiceRequestStatus.Annullato),
                PushTypes.ServiceRequestCancelled,
                EmailTemplates.ServiceRequestCancelledToSupplierPush(EmailTemplates.DefaultCulture, request.Category, comune, cancelledBy));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Supplier notification for cancelled service request {Id} could not be queued", request.Id);
        }
    }

    public Task NotifyShowcaseThreadMessageAsync(
        ServiceRequest request,
        ServiceRequestMessageAuthor author,
        CancellationToken cancellationToken) =>
        showcase.NotifyThreadMessageAsync(request, author, cancellationToken);

    public Task NotifyShowcaseOfferNotCompletedAsync(ServiceRequest request, bool timeGone, CancellationToken cancellationToken) =>
        showcase.NotifyOfferNotCompletedAsync(request, timeGone, cancellationToken);

    public Task NotifyShowcaseCancelledByCustomerAsync(ServiceRequest request, CancellationToken cancellationToken) =>
        showcase.NotifyCancelledByCustomerAsync(request, cancellationToken);

    public Task NotifyShowcaseRescheduledAsync(
        ServiceRequest request,
        DateTime previousStartUtc,
        bool proposalDropped,
        CancellationToken cancellationToken) =>
        showcase.NotifyRescheduledByCustomerAsync(request, previousStartUtc, proposalDropped, cancellationToken);

    public Task NotifyShowcaseProposalAnsweredAsync(
        ServiceRequest request,
        bool accepted,
        DateTime proposedAt,
        CancellationToken cancellationToken) =>
        showcase.NotifyProposalAnsweredByCustomerAsync(request, accepted, proposedAt, cancellationToken);

    public Task NotifyShowcaseQuoteAnsweredAsync(
        ServiceRequest request,
        bool accepted,
        ServiceRequestQuote quote,
        CancellationToken cancellationToken) =>
        showcase.NotifyQuoteAnsweredByCustomerAsync(request, accepted, quote, cancellationToken);

    /// <summary>The host reminds the supplier to answer (SP-04): email and push. One key per reminder, so a retry sends nothing twice.</summary>
    public async Task NotifyReminderAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (request.LastRemindedAt is not { } remindedAt)
            return;

        try
        {
            var supplier = await FindSupplierAsync(request, cancellationToken);
            var comune = ComuneOf(request);
            if (supplier is not null)
            {
                var email = EmailTemplates.ServiceRequestReminder(
                    EmailTemplates.DefaultCulture, supplier.LegalName, request.Category, comune, publicSiteLinks.SupplierInbox());
                emailQueue.Enqueue(supplier.Email, email, EmailTemplates.Names.ServiceRequestReminder);
            }

            QueueSupplierPush(
                request,
                PushDeliveryKeys.ServiceRequestReminder(request.Id, remindedAt),
                PushTypes.ServiceRequestReminder,
                EmailTemplates.ServiceRequestReminderPush(EmailTemplates.DefaultCulture, request.Category, comune));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reminder of service request {Id} could not be queued", request.Id);
        }
    }

    public async Task NotifyProposalAnsweredAsync(
        ServiceRequest request,
        bool accepted,
        DateTime proposedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var supplier = await FindSupplierAsync(request, cancellationToken);
            var comune = ComuneOf(request);
            if (supplier is not null)
            {
                var email = EmailTemplates.ServiceRequestProposalAnswered(
                    EmailTemplates.DefaultCulture, supplier.LegalName, request.Category, comune, accepted, publicSiteLinks.SupplierInbox());
                emailQueue.Enqueue(supplier.Email, email, EmailTemplates.Names.ServiceRequestProposalAnswered);
            }

            QueueSupplierPush(
                request,
                PushDeliveryKeys.ServiceRequestProposalAnswered(request.Id, proposedAt, accepted),
                accepted ? PushTypes.ServiceRequestProposalAccepted : PushTypes.ServiceRequestProposalRejected,
                EmailTemplates.ServiceRequestProposalAnsweredPush(EmailTemplates.DefaultCulture, request.Category, comune, accepted));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Answer to the proposal of service request {Id} could not be queued", request.Id);
        }
    }

    public async Task NotifySupplierPaidAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!await SupplierNotificationPreferences.AllowsAsync(db, request.SupplierOrgId, SupplierNotificationKind.PaymentReceived, cancellationToken))
                return;

            var supplier = await FindSupplierAsync(request, cancellationToken);
            if (supplier is not null)
            {
                var email = EmailTemplates.ServiceRequestPaid(
                    EmailTemplates.DefaultCulture,
                    supplier.LegalName,
                    request.Category,
                    PropertyNameOf(request),
                    publicSiteLinks.SupplierInbox());
                emailQueue.Enqueue(supplier.Email, email, EmailTemplates.Names.ServiceRequestPaid);
            }
            else
            {
                logger.LogWarning(
                    "ServiceRequest {Id} paid: supplier {SupplierOrgId} has no profile, no email queued",
                    request.Id, request.SupplierOrgId);
            }

            var push = EmailTemplates.ServiceRequestPaidPush(EmailTemplates.DefaultCulture, request.Category, PropertyNameOf(request));
            QueueSupplierPush(
                request, PushDeliveryKeys.ServiceRequestStatus(request.Id, request.Status), PushTypes.ServiceRequestPaid, push);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Supplier notification for paid service request {Id} could not be queued", request.Id);
        }
    }

    /// <summary>
    /// The reminder of tomorrow's job to the supplier (SP-16d), for a request of any context: e-mail and push, in Italian, with the service,
    /// the time and the comune, and a link to the request. <b>Never the address or the contact of the customer</b>: they are in the console,
    /// behind the take (decision D9), and the arguments of a queued job are no place for them. <c>true</c>: queued. <c>false</c>: it could not
    /// be queued (no profile, no time, an error). <c>null</c>: not sent, because the supplier turned «Promemoria del giorno prima» off
    /// (<see cref="SupplierNotificationKind.DayBeforeReminder"/>) or is suspended. Called by the job after it marked the request as reminded,
    /// so a retry of the job sends nothing twice. The request comes with its property (for the comune).
    /// </summary>
    public async Task<bool?> NotifySupplierDayBeforeAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.ScheduledStartUtc is not { } start)
                return false;

            if (!await SupplierNotificationPreferences.AllowsAsync(db, request.SupplierOrgId, SupplierNotificationKind.DayBeforeReminder, cancellationToken))
                return null;

            var supplier = await db.SupplierProfiles
                .AsNoTracking()
                .Where(sp => sp.OrgId == request.SupplierOrgId)
                .Select(sp => new { sp.Email, sp.LegalName, sp.Status })
                .FirstOrDefaultAsync(cancellationToken);
            if (supplier is null)
                return false;

            if (supplier.Status != SupplierStatus.Active)
                return null;

            var italian = EmailTemplates.DefaultCulture;
            var comune = ComuneOf(request);
            var serviceName = request.ServiceNameSnapshot ?? EmailTemplates.ServiceCategoryLabel(italian, request.Category);
            var queued = emailQueue.Enqueue(
                supplier.Email,
                EmailTemplates.ServiceRequestDayBefore(
                    italian, supplier.LegalName, serviceName, comune, start, request.ScheduledEndUtc, publicSiteLinks.SupplierRequest(request.Id)),
                EmailTemplates.Names.ServiceRequestDayBefore);

            QueueSupplierPush(
                request,
                PushDeliveryKeys.ServiceRequestDayBefore(request.Id),
                PushTypes.ServiceRequestDayBefore,
                EmailTemplates.ServiceRequestDayBeforePush(italian, serviceName, comune, start));
            return queued;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reminder of the day before to the supplier of service request {Id} could not be queued", request.Id);
            return false;
        }
    }

    private static string ComuneOf(ServiceRequest request) => request.Property?.City ?? request.LocationCity ?? string.Empty;

    private static string PropertyNameOf(ServiceRequest request) => request.Property?.Name ?? string.Empty;

    private Task<SupplierContact?> FindSupplierAsync(ServiceRequest request, CancellationToken cancellationToken) =>
        db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == request.SupplierOrgId)
            .Select(sp => new SupplierContact(sp.Email, sp.LegalName))
            .FirstOrDefaultAsync(cancellationToken);

    private static string HostRoute(ServiceRequest request) =>
        request.BookingId is { } bookingId ? PushRoutes.Booking(bookingId) : PushRoutes.Properties;

    private void QueueSupplierPush(ServiceRequest request, string deliveryKey, string type, PushText text)
    {
        try
        {
            pushNotifications.Enqueue(
                deliveryKey,
                PushAudience.SupplierOrg(request.SupplierOrgId),
                new PushNotificationPayload(text.Title, text.Body, type, BookingId: null, PushRoutes.SupplierRequest(request.Id), request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Push notification ({Type}) of service request {Id} could not be queued", type, request.Id);
        }
    }
}
