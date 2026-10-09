using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Everything the booking from a supplier's public showcase tells its two parties by e-mail and push (SP-10): the customer
/// checks the address (verification), then receives the receipt while the supplier receives the new request, and from then on the
/// customer hears of what the supplier does (accepted, refused, another time proposed, cancelled, lapsed) and of the reminder of
/// the day before. E-mails go through <see cref="IEmailQueue"/> (Hangfire) and the supplier's push through
/// <see cref="IPushNotificationService"/>: nothing is sent inside the request of the caller.
/// </summary>
/// <remarks>
/// <para><b>Who it writes to.</b> The customer, in the language it chose (<see cref="ServiceCustomerContact.Locale"/>); the
/// supplier, in Italian, with the comune and "Nome C." only (decision D9). There is no host in a showcase request, so
/// <see cref="ServiceRequestNotifier"/> hands it every notification that would go to one.</para>
/// <para>Called only <b>after</b> the change is saved, and only by the winner of a transition (<c>xmin</c>): the loser of a race
/// notifies nobody. A failure here is logged and never turned into an error for the caller, since the change is already saved.
/// Nothing personal goes in a log: ids and the name of the template only.</para>
/// <para>A status with no mail to the customer (started, completed, paid) sends nothing and logs nothing: it is not an error.</para>
/// </remarks>
public sealed class ShowcaseBookingNotifier(
    AppDbContext db,
    IServiceCustomerReader customers,
    IEmailQueue emailQueue,
    PublicSiteLinks publicSiteLinks,
    IPushNotificationService pushNotifications,
    ILogger<ShowcaseBookingNotifier> logger)
{
    private sealed record SupplierContact(string Email, string LegalName, string? Slug);

    // ─── The booking is made: the customer checks the address ────────────────────────────────────────────────────────

    /// <summary>
    /// The verification e-mail, rendered <b>before</b> the hold is saved: a missing <c>App:PublicSiteBaseUrl</c> is a configuration
    /// error that must stop the booking, not a wrong link in an e-mail.
    /// </summary>
    public EmailContent RenderVerification(
        SupplierProfile supplier,
        ShowcaseBookingPayload payload,
        DateTime startUtc,
        Guid holdId,
        string token,
        int validMinutes) =>
        EmailTemplates.SupplierBookingVerification(
            EmailTemplates.CultureOf(payload.Locale),
            payload.FullName,
            supplier.LegalName,
            payload.ServiceName,
            startUtc,
            validMinutes,
            publicSiteLinks.SupplierBookingConfirmation(SlugOf(supplier), holdId, token));

    /// <summary>
    /// Queues the e-mail rendered by <see cref="RenderVerification"/> to the address the customer gave. <c>false</c> when it could
    /// not be queued (the e-mail provider is not configured, the queue is down): the booking then lets the hold go.
    /// </summary>
    public bool QueueVerification(string to, EmailContent email) =>
        emailQueue.Enqueue(to, email, EmailTemplates.Names.SupplierBookingVerification);

    // ─── The address is checked: the receipt and the new request ─────────────────────────────────────────────────────

    /// <summary>
    /// The address is checked and the request exists: the receipt to the customer, and the new request to the supplier (e-mail
    /// and push, comune only). Never throws.
    /// </summary>
    public void QueueRequestReceived(
        SupplierProfile supplier,
        ServiceRequest request,
        ServiceCustomer customer,
        DateTime respondByUtc)
    {
        try
        {
            var culture = EmailTemplates.CultureOf(customer.Locale);
            var slug = SlugOf(supplier);
            var serviceName = request.ServiceNameSnapshot ?? EmailTemplates.ServiceCategoryLabel(culture, request.Category);
            var start = request.ScheduledStartUtc!.Value;

            emailQueue.Enqueue(
                customer.Email,
                EmailTemplates.SupplierBookingReceipt(
                    culture,
                    customer.FullName,
                    supplier.LegalName,
                    serviceName,
                    start,
                    request.PublicCode!,
                    respondByUtc,
                    request.EstimatedAmountCents,
                    publicSiteLinks.SupplierBookingRequest(slug, request.PublicCode)),
                EmailTemplates.Names.SupplierBookingReceipt);

            var italian = EmailTemplates.DefaultCulture;
            var comune = request.LocationCity ?? string.Empty;
            emailQueue.Enqueue(
                supplier.Email,
                EmailTemplates.SupplierBookingNewRequest(
                    italian,
                    supplier.LegalName,
                    serviceName,
                    comune,
                    ShowcaseBookingRules.AbbreviateName(customer.FullName),
                    start,
                    request.EstimatedAmountCents,
                    respondByUtc,
                    publicSiteLinks.SupplierInbox()),
                EmailTemplates.Names.SupplierBookingNewRequest);

            var push = EmailTemplates.ServiceRequestCreatedPush(italian, request.Category, comune);
            pushNotifications.Enqueue(
                PushDeliveryKeys.ServiceRequestCreated(request.Id),
                PushAudience.SupplierOrg(request.SupplierOrgId),
                new PushNotificationPayload(
                    push.Title, push.Body, PushTypes.ServiceRequestCreated, BookingId: null, PushRoutes.Properties, request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Notifications of the showcase request {Id} could not be queued", request.Id);
        }
    }

    // ─── What the supplier did, or CasaZen did for it: the customer ──────────────────────────────────────────────────

    /// <summary>
    /// The status of the request changed by the supplier (taken, refused, cancelled) or by CasaZen (no answer in time): the mail to
    /// the customer. Started, completed and paid send nothing. Never throws.
    /// </summary>
    public async Task NotifyCustomerOfStatusAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Status is not (ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.Rifiutato or ServiceRequestStatus.Annullato))
                return;

            // A cancellation the customer itself made (SP-11) is not told back to it.
            if (request.Status == ServiceRequestStatus.Annullato
                && request.CancelledBy is not (ServiceRequestActorParty.Supplier or ServiceRequestActorParty.System))
            {
                return;
            }

            var (contact, supplier) = await FindPartiesAsync(request, cancellationToken);
            if (contact is null || supplier is null)
                return;

            var culture = EmailTemplates.CultureOf(contact.Locale);
            var serviceName = request.ServiceNameSnapshot ?? EmailTemplates.ServiceCategoryLabel(culture, request.Category);
            var start = request.ScheduledStartUtc ?? request.CreatedAt;
            var showcaseUrl = SupplierPageOf(supplier);

            switch (request.Status)
            {
                case ServiceRequestStatus.PresoInCarico:
                    emailQueue.Enqueue(
                        contact.Email,
                        EmailTemplates.SupplierBookingAccepted(
                            culture,
                            contact.FullName,
                            supplier.LegalName,
                            serviceName,
                            start,
                            request.QuotedAmountCents,
                            request.EstimatedAmountCents,
                            ServiceRequestReminderRules.WillBeSent(start, request.TakenAt ?? DateTime.MaxValue),
                            RequestUrlOf(supplier, request)),
                        EmailTemplates.Names.SupplierBookingAccepted);
                    break;

                case ServiceRequestStatus.Rifiutato:
                    emailQueue.Enqueue(
                        contact.Email,
                        EmailTemplates.SupplierBookingDeclined(
                            culture, contact.FullName, supplier.LegalName, serviceName, start, request.RejectionReason, showcaseUrl),
                        EmailTemplates.Names.SupplierBookingDeclined);
                    break;

                case ServiceRequestStatus.Annullato when request.CancelledBy == ServiceRequestActorParty.System:
                    emailQueue.Enqueue(
                        contact.Email,
                        EmailTemplates.SupplierBookingExpired(culture, contact.FullName, supplier.LegalName, serviceName, start, showcaseUrl),
                        EmailTemplates.Names.SupplierBookingExpired);
                    break;

                default:
                    emailQueue.Enqueue(
                        contact.Email,
                        EmailTemplates.SupplierBookingCancelled(
                            culture, contact.FullName, supplier.LegalName, serviceName, start, request.CancellationReason, showcaseUrl),
                        EmailTemplates.Names.SupplierBookingCancelled);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Customer e-mail for showcase request {Id} ({Status}) could not be queued", request.Id, request.Status);
        }
    }

    /// <summary>The supplier proposed another time: the mail to the customer, with the time by which it has to answer. Never throws.</summary>
    public async Task NotifyCustomerOfProposalAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (request is not { ProposedStartUtc: not null, ResponseDueAt: { } answerBy })
            return;

        try
        {
            var (contact, supplier) = await FindPartiesAsync(request, cancellationToken);
            if (contact is null || supplier is null)
                return;

            var culture = EmailTemplates.CultureOf(contact.Locale);
            emailQueue.Enqueue(
                contact.Email,
                EmailTemplates.SupplierBookingTimeProposed(
                    culture,
                    contact.FullName,
                    supplier.LegalName,
                    request.ServiceNameSnapshot ?? EmailTemplates.ServiceCategoryLabel(culture, request.Category),
                    request.ScheduledStartUtc ?? request.CreatedAt,
                    request.ProposedStartUtc!.Value,
                    request.ProposalMessage,
                    answerBy,
                    RequestUrlOf(supplier, request)),
                EmailTemplates.Names.SupplierBookingTimeProposed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Customer e-mail for the time proposed on showcase request {Id} could not be queued", request.Id);
        }
    }

    // ─── The day before: the reminder ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The reminder of the day before to the customer of a request the supplier took. <c>false</c> when it could not be queued
    /// (no customer to write to, anonymized, an error): the caller only counts and logs it, since the request was marked as
    /// reminded <b>before</b> this call (a reminder is sent at most once, never twice).
    /// </summary>
    public async Task<bool> TryQueueReminderAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var (contact, supplier) = await FindPartiesAsync(request, cancellationToken);
            if (contact is null || supplier is null || request.ScheduledStartUtc is not { } start)
                return false;

            var culture = EmailTemplates.CultureOf(contact.Locale);
            return emailQueue.Enqueue(
                contact.Email,
                EmailTemplates.SupplierBookingReminder(
                    culture,
                    contact.FullName,
                    supplier.LegalName,
                    request.ServiceNameSnapshot ?? EmailTemplates.ServiceCategoryLabel(culture, request.Category),
                    start,
                    RequestUrlOf(supplier, request)),
                EmailTemplates.Names.SupplierBookingReminder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reminder of showcase request {Id} could not be queued", request.Id);
            return false;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<(ServiceCustomerContact? Contact, SupplierContact? Supplier)> FindPartiesAsync(
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CustomerId is not { } customerId)
            return (null, null);

        var contact = await customers.FindContactAsync(request.SupplierOrgId, customerId, cancellationToken);
        if (contact is null || contact.Anonymized || string.IsNullOrWhiteSpace(contact.Email))
            return (null, null);

        // SupplierProfile is keyed by the supplier org and not tenant-filtered; scoped by the request's supplier org.
        var supplier = await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == request.SupplierOrgId)
            .Select(sp => new SupplierContact(sp.Email, sp.LegalName, sp.ShowcaseSlug))
            .FirstOrDefaultAsync(cancellationToken);
        return (contact, supplier);
    }

    private static string SlugOf(SupplierProfile supplier) =>
        string.IsNullOrWhiteSpace(supplier.ShowcaseSlug)
            ? throw new InvalidOperationException($"Supplier {supplier.OrgId} has no showcase slug: a booking link cannot be built")
            : supplier.ShowcaseSlug;

    private string SupplierPageOf(SupplierContact supplier) =>
        publicSiteLinks.SupplierShowcase(supplier.Slug ?? throw new InvalidOperationException("The supplier has no showcase slug"));

    private string RequestUrlOf(SupplierContact supplier, ServiceRequest request) =>
        publicSiteLinks.SupplierBookingRequest(
            supplier.Slug ?? throw new InvalidOperationException("The supplier has no showcase slug"),
            request.PublicCode);

}
