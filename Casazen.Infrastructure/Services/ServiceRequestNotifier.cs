using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
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
/// </remarks>
public sealed class ServiceRequestNotifier(
    AppDbContext db,
    IEmailQueue emailQueue,
    PublicSiteLinks publicSiteLinks,
    IPushNotificationService pushNotifications,
    IOptions<ServiceRequestOptions> options,
    ILogger<ServiceRequestNotifier> logger)
{
    private sealed record SupplierContact(string Email, string LegalName);

    // ─── New request: the supplier ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The email to the supplier for a new request, rendered <b>before</b> the request is saved: a missing
    /// <c>App:PublicSiteBaseUrl</c> is a configuration error that must stop the request, not a wrong link in an email.
    /// </summary>
    public EmailContent RenderCreatedEmail(ServiceRequest request, string supplierName, string comune) =>
        EmailTemplates.ServiceRequestCreated(
            EmailTemplates.DefaultCulture,
            supplierName,
            request.Category,
            comune,
            publicSiteLinks.SupplierInbox(),
            request.ScheduledStartUtc,
            request.EstimatedAmountCents);

    /// <summary>Queues the email rendered by <see cref="RenderCreatedEmail"/> and the push of the new request.</summary>
    public void QueueCreated(ServiceRequest request, string supplierEmail, EmailContent email, string comune)
    {
        emailQueue.Enqueue(supplierEmail, email, EmailTemplates.Names.ServiceRequestCreated);
        QueueSupplierPush(
            request,
            PushDeliveryKeys.ServiceRequestCreated(request.Id),
            PushTypes.ServiceRequestCreated,
            EmailTemplates.ServiceRequestCreatedPush(EmailTemplates.DefaultCulture, request.Category, comune));
    }

    // ─── What the supplier did: the host ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Email and push to the host after a status change made by the supplier (take, start, completion, rejection) or by CasaZen
    /// (cancellation for no answer), or a cancellation by the supplier. The push key is the transition, so the host gets one push.
    /// </summary>
    public async Task NotifyHostAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
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
                request.Property.Name,
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
                EmailTemplates.DefaultCulture, request.Status, request.Category, request.Property.Name, request.CancelledBy);
            if (type is null || push is null)
                return;

            pushNotifications.Enqueue(
                PushDeliveryKeys.ServiceRequestStatus(request.Id, request.Status),
                PushAudience.PropertyHosts(request.PropertyId),
                new PushNotificationPayload(push.Title, push.Body, type, request.BookingId, HostRoute(request), request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Push notification for service request {Id} ({Status}) could not be queued", request.Id, request.Status);
        }
    }

    /// <summary>
    /// The supplier proposed another time (SP-04): email and push to the host. The email links to the page of the stay when the
    /// request has one.
    /// </summary>
    public async Task NotifyTimeProposedAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (request is not { ProposedStartUtc: { } start, ProposedEndUtc: { } end, ProposedAt: { } proposedAt })
            return;

        try
        {
            var hostUrl = request.BookingId is { } bookingId ? publicSiteLinks.HostBooking(bookingId) : null;
            var email = EmailTemplates.ServiceRequestTimeProposed(
                EmailTemplates.DefaultCulture, request.Category, request.Property.Name, start, end, request.ProposalMessage, hostUrl);
            var hostEmail = await db.Orgs
                .AsNoTracking()
                .Where(o => o.Id == request.OrgId)
                .Select(o => o.ContactEmail)
                .FirstOrDefaultAsync(cancellationToken);
            emailQueue.Enqueue(hostEmail, email, EmailTemplates.Names.ServiceRequestTimeProposed);

            var push = EmailTemplates.ServiceRequestTimeProposedPush(EmailTemplates.DefaultCulture, request.Category, request.Property.Name);
            pushNotifications.Enqueue(
                PushDeliveryKeys.ServiceRequestTimeProposed(request.Id, proposedAt),
                PushAudience.PropertyHosts(request.PropertyId),
                new PushNotificationPayload(
                    push.Title, push.Body, PushTypes.ServiceRequestTimeProposed, request.BookingId, HostRoute(request), request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Notification of the time proposed for service request {Id} could not be queued", request.Id);
        }
    }

    // ─── What the host did: the supplier ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Email and push to the supplier when the request was cancelled by the host, or by CasaZen because nobody answered in
    /// time (<see cref="ServiceRequest.CancelledBy"/> says which). The supplier is told whatever its status.
    /// </summary>
    public async Task NotifySupplierCancelledAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (request.CancelledBy is not { } cancelledBy || cancelledBy == ServiceRequestActorParty.Supplier)
            return;

        try
        {
            var supplier = await FindSupplierAsync(request, cancellationToken);
            var comune = request.Property.City;
            if (supplier is not null)
            {
                var email = EmailTemplates.ServiceRequestCancelledToSupplier(
                    EmailTemplates.DefaultCulture,
                    supplier.LegalName,
                    request.Category,
                    comune,
                    cancelledBy,
                    request.CancellationReason,
                    publicSiteLinks.SupplierInbox());
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

    /// <summary>The host reminds the supplier to answer (SP-04): email and push. One key per reminder, so a retry sends nothing twice.</summary>
    public async Task NotifyReminderAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (request.LastRemindedAt is not { } remindedAt)
            return;

        try
        {
            var supplier = await FindSupplierAsync(request, cancellationToken);
            var comune = request.Property.City;
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

    /// <summary>
    /// The host accepted or declined the time the supplier proposed (SP-04): email and push to the supplier.
    /// <paramref name="proposedAt"/> is the instant of the proposal that was answered (the request does not keep it anymore).
    /// </summary>
    public async Task NotifyProposalAnsweredAsync(
        ServiceRequest request,
        bool accepted,
        DateTime proposedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var supplier = await FindSupplierAsync(request, cancellationToken);
            var comune = request.Property.City;
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

    /// <summary>
    /// Email and push to the supplier after the host marked a request as paid (SU-09). The supplier is notified whatever its
    /// status: a suspended supplier is still owed what it completed.
    /// </summary>
    public async Task NotifySupplierPaidAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var supplier = await FindSupplierAsync(request, cancellationToken);
            if (supplier is not null)
            {
                var email = EmailTemplates.ServiceRequestPaid(
                    EmailTemplates.DefaultCulture,
                    supplier.LegalName,
                    request.Category,
                    request.Property.Name,
                    publicSiteLinks.SupplierInbox());
                emailQueue.Enqueue(supplier.Email, email, EmailTemplates.Names.ServiceRequestPaid);
            }
            else
            {
                logger.LogWarning(
                    "ServiceRequest {Id} paid: supplier {SupplierOrgId} has no profile, no email queued",
                    request.Id, request.SupplierOrgId);
            }

            var push = EmailTemplates.ServiceRequestPaidPush(EmailTemplates.DefaultCulture, request.Category, request.Property.Name);
            QueueSupplierPush(
                request, PushDeliveryKeys.ServiceRequestStatus(request.Id, request.Status), PushTypes.ServiceRequestPaid, push);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Supplier notification for paid service request {Id} could not be queued", request.Id);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    // SupplierProfile is keyed by the supplier org and not tenant-filtered; scoped by the request's supplier org.
    private Task<SupplierContact?> FindSupplierAsync(ServiceRequest request, CancellationToken cancellationToken) =>
        db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == request.SupplierOrgId)
            .Select(sp => new SupplierContact(sp.Email, sp.LegalName))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// A request tied to a stay opens it; one without a stay opens the property list: the app has no service request screen
    /// (MO-03, A6-19).
    /// </summary>
    private static string HostRoute(ServiceRequest request) =>
        request.BookingId is { } bookingId ? PushRoutes.Booking(bookingId) : PushRoutes.Properties;

    /// <summary>
    /// A push to the supplier org (A6-08). The stay belongs to the host (the supplier cannot open it), so the push carries no
    /// booking and opens the property list of the app, which has no supplier screens yet; <c>serviceRequestId</c> is in the
    /// data for a supplier app.
    /// </summary>
    private void QueueSupplierPush(ServiceRequest request, string deliveryKey, string type, PushText text)
    {
        try
        {
            pushNotifications.Enqueue(
                deliveryKey,
                PushAudience.SupplierOrg(request.SupplierOrgId),
                new PushNotificationPayload(text.Title, text.Body, type, BookingId: null, PushRoutes.Properties, request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Push notification ({Type}) of service request {Id} could not be queued", type, request.Id);
        }
    }
}
