using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

// What the backup suppliers of a request are told, and what the host is told when one of them takes the job (SP-BKP). Same rules as the
// rest of the notifier: called after the change is saved, by the winner only, never an error for the caller, and (decision D9) the supplier
// is told the comune, never the property, the street or the host's notes. The two notices that offer new work to a backup (it is a backup;
// the second window opened) follow the supplier's switch «Nuove richieste» like the notice of a new request (SP-16d); the ones that follow
// from what the supplier did or from the end of the race are always sent.
public sealed partial class ServiceRequestNotifier
{
    public async Task NotifyBackupsInvitedAsync(
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
                if (!await SupplierNotificationPreferences.AllowsAsync(db, request.SupplierOrgId, SupplierNotificationKind.NewRequest, cancellationToken))
                    continue;

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
    }

    public async Task NotifyBackupActiveAsync(ServiceRequest request, DateTime backupDueAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            if (!await SupplierNotificationPreferences.AllowsAsync(db, request.SupplierOrgId, SupplierNotificationKind.NewRequest, cancellationToken))
                return;

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
