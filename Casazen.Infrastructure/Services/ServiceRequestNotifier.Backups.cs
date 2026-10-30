using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

// What the backup suppliers of a request are told, and what the host is told when one of them takes the job (SP-BKP). Same rules as the
// rest of the notifier: called after the change is saved, by the winner only, never an error for the caller, and (decision D9) the supplier
// is told the comune, never the property, the street or the host's notes.
public sealed partial class ServiceRequestNotifier
{
    /// <summary>
    /// The backups of a new request are told they are backups (SP-BKP): the job is theirs only if the primary supplier does not take it by
    /// <paramref name="primaryDueAtUtc"/>, and they can accept as a backup right away. One email and one push each.
    /// </summary>
    public Task NotifyBackupsInvitedAsync(
        IReadOnlyList<(ServiceRequest Request, SupplierProfile Supplier)> backups,
        string comune,
        DateTime primaryDueAtUtc,
        CancellationToken cancellationToken)
    {
        foreach (var (request, supplier) in backups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var email = EmailTemplates.ServiceRequestBackupInvited(
                    EmailTemplates.DefaultCulture,
                    supplier.LegalName,
                    request.Category,
                    comune,
                    publicSiteLinks.SupplierInbox(),
                    primaryDueAtUtc,
                    request.ScheduledStartUtc,
                    request.EstimatedAmountCents);
                emailQueue.Enqueue(supplier.Email, email, EmailTemplates.Names.ServiceRequestBackupInvited);

                QueueSupplierPush(
                    request,
                    PushDeliveryKeys.ServiceRequestBackup(request.Id, "invited"),
                    PushTypes.ServiceRequestBackupInvited,
                    EmailTemplates.ServiceRequestBackupInvitedPush(EmailTemplates.DefaultCulture, request.Category, comune));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Backup notification for service request {Id} could not be queued", request.Id);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>The second window of the group opened (SP-BKP): the backup can take the job now, until <paramref name="backupDueAtUtc"/>.</summary>
    public async Task NotifyBackupActiveAsync(ServiceRequest request, DateTime backupDueAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            var supplier = await FindSupplierAsync(request, cancellationToken);
            var comune = ComuneOf(request);
            if (supplier is not null)
            {
                var email = EmailTemplates.ServiceRequestBackupActive(
                    EmailTemplates.DefaultCulture, supplier.LegalName, request.Category, comune, publicSiteLinks.SupplierInbox(), backupDueAtUtc);
                emailQueue.Enqueue(supplier.Email, email, EmailTemplates.Names.ServiceRequestBackupActive);
            }

            QueueSupplierPush(
                request,
                PushDeliveryKeys.ServiceRequestBackup(request.Id, "active"),
                PushTypes.ServiceRequestBackupActive,
                EmailTemplates.ServiceRequestBackupActivePush(EmailTemplates.DefaultCulture, request.Category, comune));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Backup 'active' notification for service request {Id} could not be queued", request.Id);
        }
    }

    /// <summary>The job went to this backup, which had accepted (SP-BKP): it did not act at that moment, so it is told.</summary>
    public async Task NotifyBackupWonAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var supplier = await FindSupplierAsync(request, cancellationToken);
            var comune = ComuneOf(request);
            if (supplier is not null)
            {
                var email = EmailTemplates.ServiceRequestBackupWon(
                    EmailTemplates.DefaultCulture,
                    supplier.LegalName,
                    request.Category,
                    comune,
                    publicSiteLinks.SupplierInbox(),
                    request.ScheduledStartUtc,
                    request.EstimatedAmountCents);
                emailQueue.Enqueue(supplier.Email, email, EmailTemplates.Names.ServiceRequestBackupWon);
            }

            QueueSupplierPush(
                request,
                PushDeliveryKeys.ServiceRequestBackup(request.Id, "won"),
                PushTypes.ServiceRequestBackupWon,
                EmailTemplates.ServiceRequestBackupWonPush(EmailTemplates.DefaultCulture, request.Category, comune));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Backup 'won' notification for service request {Id} could not be queued", request.Id);
        }
    }

    /// <summary>A backup's request was closed (SP-BKP): the job went to the primary or to another backup, the host cancelled, or its time was no longer free.</summary>
    public async Task NotifyBackupClosedAsync(ServiceRequest request, ServiceRequestBackupClosedKind kind, CancellationToken cancellationToken)
    {
        try
        {
            var supplier = await FindSupplierAsync(request, cancellationToken);
            var comune = ComuneOf(request);
            if (supplier is not null)
            {
                var email = EmailTemplates.ServiceRequestBackupClosed(
                    EmailTemplates.DefaultCulture, supplier.LegalName, request.Category, comune, kind, publicSiteLinks.SupplierInbox());
                emailQueue.Enqueue(supplier.Email, email, EmailTemplates.Names.ServiceRequestBackupClosed);
            }

            QueueSupplierPush(
                request,
                PushDeliveryKeys.ServiceRequestBackup(request.Id, "closed"),
                PushTypes.ServiceRequestBackupClosed,
                EmailTemplates.ServiceRequestBackupClosedPush(EmailTemplates.DefaultCulture, request.Category, comune));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Backup 'closed' notification for service request {Id} could not be queued", request.Id);
        }
    }

    /// <summary>
    /// Nobody took the job of a group (SP-BKP), to the host: the "nobody answered" email and push of a request cancelled by CasaZen, also when
    /// the request the host follows is one the primary supplier refused earlier (its own status stays <c>Rifiutato</c>, but the host has not
    /// been told that the backups did not take the job either). Same push key as the cancellation, so a request is told once.
    /// </summary>
    public async Task NotifyHostNoResponseAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (request.RentalContext == ServiceRequestRentalContext.Showcase)
            return;

        try
        {
            var email = EmailTemplates.ServiceRequestStatusChanged(
                EmailTemplates.DefaultCulture,
                ServiceRequestStatus.Annullato,
                request.Category,
                PropertyNameOf(request),
                reason: null,
                ServiceRequestActorParty.System,
                completion: null,
                request.ScheduledStartUtc);
            if (email is not null)
            {
                var hostEmail = await db.Orgs
                    .AsNoTracking()
                    .Where(o => o.Id == request.OrgId)
                    .Select(o => o.ContactEmail)
                    .FirstOrDefaultAsync(cancellationToken);
                emailQueue.Enqueue(hostEmail, email, EmailTemplates.Names.ServiceRequestStatusChanged);
            }

            var push = EmailTemplates.ServiceRequestStatusPush(
                EmailTemplates.DefaultCulture,
                ServiceRequestStatus.Annullato,
                request.Category,
                PropertyNameOf(request),
                ServiceRequestActorParty.System);
            if (push is not null && request.PropertyId is { } propertyId)
            {
                pushNotifications.Enqueue(
                    PushDeliveryKeys.ServiceRequestStatus(request.Id, ServiceRequestStatus.Annullato),
                    PushAudience.PropertyHosts(propertyId),
                    new PushNotificationPayload(push.Title, push.Body, PushTypes.ServiceRequestCancelled, request.BookingId, HostRoute(request), request.Id));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Host 'nobody answered' notification for service request {Id} could not be queued", request.Id);
        }
    }

    /// <summary>
    /// A backup took the job the primary supplier did not take in time (SP-BKP), to the host: who has it now. It takes the place of the
    /// "taken" email and push of <see cref="NotifyHostAsync"/> (same push type and key, so the host gets one push for the take).
    /// <paramref name="request"/> is the request that was taken, with its property and its supplier org.
    /// </summary>
    public async Task NotifyHostBackupTookOverAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var supplierName = request.SupplierOrg?.DisplayName is { Length: > 0 } displayName ? displayName : request.SupplierOrg?.Name ?? string.Empty;
            var hostUrl = request.BookingId is { } bookingId ? publicSiteLinks.HostBooking(bookingId) : null;
            var email = EmailTemplates.ServiceRequestTakenByBackup(
                EmailTemplates.DefaultCulture, request.Category, PropertyNameOf(request), supplierName, request.ScheduledStartUtc, hostUrl);
            var hostEmail = await db.Orgs
                .AsNoTracking()
                .Where(o => o.Id == request.OrgId)
                .Select(o => o.ContactEmail)
                .FirstOrDefaultAsync(cancellationToken);
            emailQueue.Enqueue(hostEmail, email, EmailTemplates.Names.ServiceRequestTakenByBackup);

            if (request.PropertyId is { } propertyId)
            {
                var push = EmailTemplates.ServiceRequestTakenByBackupPush(
                    EmailTemplates.DefaultCulture, request.Category, PropertyNameOf(request), supplierName);
                pushNotifications.Enqueue(
                    PushDeliveryKeys.ServiceRequestStatus(request.Id, ServiceRequestStatus.PresoInCarico),
                    PushAudience.PropertyHosts(propertyId),
                    new PushNotificationPayload(push.Title, push.Body, PushTypes.ServiceRequestTaken, request.BookingId, HostRoute(request), request.Id));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Host notification for service request {Id} taken by a backup could not be queued", request.Id);
        }
    }
}
