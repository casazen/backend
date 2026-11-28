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
/// Everything the booking from a supplier's public showcase tells its two parties by e-mail and push (SP-10): the customer
/// checks the address (verification), then receives the receipt while the supplier receives the new request, and from then on the
/// customer hears of what the supplier does (accepted, refused, another time proposed, cancelled, lapsed) and of the reminder of
/// the day before; and (SP-11) the supplier hears of what the customer does (cancels, moves the time, answers a proposed time,
/// lets a proposed time lapse) and the customer receives the receipt of its cancellation. E-mails go through
/// <see cref="IEmailQueue"/> (Hangfire) and the supplier's push through <see cref="IPushNotificationService"/>: nothing is sent
/// inside the request of the caller.
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
    IOptions<ShowcaseBookingOptions> options,
    ILogger<ShowcaseBookingNotifier> logger,
    IShowcaseAlternativesService? alternatives = null)
{
    private sealed record SupplierContact(string Email, string LegalName, string? Slug);

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

    public bool QueueVerification(string to, EmailContent email) =>
        emailQueue.Enqueue(to, email, EmailTemplates.Names.SupplierBookingVerification);

    /// <summary>
    /// The address is checked and the request exists: the receipt to the customer, and the new request to the supplier (e-mail
    /// and push, comune only), unless the supplier turned off «Nuove richieste» (SP-16d, <see cref="SupplierNotificationKind.NewRequest"/>):
    /// the customer's receipt does not depend on it. Never throws.
    /// </summary>
    public async Task QueueRequestReceivedAsync(
        SupplierProfile supplier,
        ServiceRequest request,
        ServiceCustomer customer,
        DateTime respondByUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var culture = EmailTemplates.CultureOf(customer.Locale);
            var slug = SlugOf(supplier);
            var serviceName = request.ServiceNameSnapshot ?? EmailTemplates.ServiceCategoryLabel(culture, request.Category);
            var start = request.ScheduledStartUtc!.Value;

            var cardBlocked = request.PaymentMode == ServiceRequestPaymentMode.Online;

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
                    publicSiteLinks.SupplierBookingRequest(slug, request.PublicCode),
                    cardBlocked: cardBlocked),
                EmailTemplates.Names.SupplierBookingReceipt);

            if (!await SupplierNotificationPreferences.AllowsAsync(db, request.SupplierOrgId, SupplierNotificationKind.NewRequest, cancellationToken))
                return;

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
                    publicSiteLinks.SupplierInbox(),
                    cardBlocked: cardBlocked),
                EmailTemplates.Names.SupplierBookingNewRequest);

            var push = EmailTemplates.ServiceRequestCreatedPush(italian, request.Category, comune);
            pushNotifications.Enqueue(
                PushDeliveryKeys.ServiceRequestCreated(request.Id),
                PushAudience.SupplierOrg(request.SupplierOrgId),
                new PushNotificationPayload(
                    push.Title, push.Body, PushTypes.ServiceRequestCreated, BookingId: null, PushRoutes.SupplierRequest(request.Id), request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Notifications of the showcase request {Id} could not be queued", request.Id);
        }
    }

    public EmailContent RenderInquiryVerification(
        SupplierProfile supplier,
        ShowcaseInquiryPayload payload,
        Guid inquiryId,
        string token,
        int validMinutes) =>
        EmailTemplates.SupplierInquiryVerification(
            EmailTemplates.CultureOf(payload.Locale),
            payload.FullName,
            supplier.LegalName,
            publicSiteLinks.SupplierInquiryConfirmation(SlugOf(supplier), inquiryId, token),
            validMinutes);

    public bool QueueInquiryVerification(string to, EmailContent email) =>
        emailQueue.Enqueue(to, email, EmailTemplates.Names.SupplierInquiryVerification);

    public async Task QueueInquiryReceivedAsync(
        SupplierProfile supplier,
        ServiceRequest request,
        ServiceCustomer customer,
        DateTime respondByUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var culture = EmailTemplates.CultureOf(customer.Locale);

            emailQueue.Enqueue(
                customer.Email,
                EmailTemplates.SupplierInquiryReceipt(
                    culture,
                    customer.FullName,
                    supplier.LegalName,
                    request.PublicCode!,
                    respondByUtc,
                    publicSiteLinks.SupplierBookingRequest(SlugOf(supplier), request.PublicCode)),
                EmailTemplates.Names.SupplierInquiryReceipt);

            if (!await SupplierNotificationPreferences.AllowsAsync(
                    db,
                    request.SupplierOrgId,
                    SupplierNotificationKind.NewRequest,
                    cancellationToken))
            {
                return;
            }

            var italian = EmailTemplates.DefaultCulture;
            var comune = request.LocationCity ?? string.Empty;
            emailQueue.Enqueue(
                supplier.Email,
                EmailTemplates.SupplierInquiryNewRequest(
                    italian,
                    supplier.LegalName,
                    request.ServiceNameSnapshot,
                    comune,
                    ShowcaseBookingRules.AbbreviateName(customer.FullName),
                    respondByUtc,
                    publicSiteLinks.SupplierInbox()),
                EmailTemplates.Names.SupplierInquiryNewRequest);

            var push = EmailTemplates.ServiceRequestCreatedPush(italian, request.Category, comune);
            pushNotifications.Enqueue(
                PushDeliveryKeys.ServiceRequestCreated(request.Id),
                PushAudience.SupplierOrg(request.SupplierOrgId),
                new PushNotificationPayload(
                    push.Title, push.Body, PushTypes.ServiceRequestCreated, BookingId: null, PushRoutes.SupplierRequest(request.Id), request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Notifications of the showcase request on measure {Id} could not be queued", request.Id);
        }
    }

    public async Task NotifyCustomerOfStatusAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Status is not (ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.Rifiutato or ServiceRequestStatus.Annullato))
                return;

            if (request.Status == ServiceRequestStatus.Annullato
                && request.CancelledBy is not (ServiceRequestActorParty.Supplier or ServiceRequestActorParty.System))
            {
                return;
            }

            var (contact, supplier) = await FindPartiesAsync(request, cancellationToken);
            if (contact is null || supplier is null)
                return;

            var culture = EmailTemplates.CultureOf(contact.Locale);

            // SP-10c: a request CasaZen closed because the supplier did not answer in time comes with the other suppliers that could help.
            var alternativeLinks = AlternativeLinks(
                ShowcaseAlternativeRules.Offers(request.Status, request.CancelledBy, request.CancellationReason)
                    ? await FindAlternativesAsync(request, cancellationToken)
                    : null);

            if (request.Kind == ServiceRequestKind.Custom)
            {
                QueueInquiryStatus(request, contact, supplier, culture, alternativeLinks);
                return;
            }

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
                            RequestUrlOf(supplier, request),
                            // SP-15c-a: the take charged the card that was blocked (it cannot be taken otherwise).
                            cardCharged: request.PaymentMode == ServiceRequestPaymentMode.Online),
                        EmailTemplates.Names.SupplierBookingAccepted);
                    break;

                case ServiceRequestStatus.Rifiutato:
                    emailQueue.Enqueue(
                        contact.Email,
                        EmailTemplates.SupplierBookingDeclined(
                            culture, contact.FullName, supplier.LegalName, serviceName, start, request.RejectionReason, showcaseUrl),
                        EmailTemplates.Names.SupplierBookingDeclined);
                    break;

                case ServiceRequestStatus.Annullato when request.CancelledBy == ServiceRequestActorParty.System
                                                         && request.CancellationReason == ServiceRequestCancellationReasons.ProposalNotAnswered:
                    emailQueue.Enqueue(
                        contact.Email,
                        EmailTemplates.SupplierBookingProposalExpired(culture, contact.FullName, supplier.LegalName, serviceName, start, showcaseUrl),
                        EmailTemplates.Names.SupplierBookingProposalExpired);
                    break;

                case ServiceRequestStatus.Annullato when request.CancelledBy == ServiceRequestActorParty.System
                                                         && request.CancellationReason == ServiceRequestCancellationReasons.QuoteNotAnswered:
                    emailQueue.Enqueue(
                        contact.Email,
                        EmailTemplates.SupplierBookingQuoteExpired(culture, contact.FullName, supplier.LegalName, serviceName, start, showcaseUrl),
                        EmailTemplates.Names.SupplierBookingQuoteExpired);
                    break;

                case ServiceRequestStatus.Annullato when request.CancelledBy == ServiceRequestActorParty.System:
                    emailQueue.Enqueue(
                        contact.Email,
                        EmailTemplates.SupplierBookingExpired(culture, contact.FullName, supplier.LegalName, serviceName, start, showcaseUrl, alternativeLinks),
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

    public async Task NotifyCustomerOfQuoteAsync(ServiceRequest request, ServiceRequestQuote quote, CancellationToken cancellationToken)
    {
        try
        {
            var (contact, supplier) = await FindPartiesAsync(request, cancellationToken);
            if (contact is null || supplier is null)
                return;

            var culture = EmailTemplates.CultureOf(contact.Locale);
            emailQueue.Enqueue(
                contact.Email,
                EmailTemplates.ServiceRequestQuoteReceived(
                    culture,
                    contact.FullName,
                    supplier.LegalName,
                    ServiceNameOf(request, culture),
                    quote.ScheduledStartUtc ?? request.ScheduledStartUtc ?? request.CreatedAt,
                    quote is { ScheduledStartUtc: not null, ScheduledEndUtc: not null } ? quote.ScheduledEndUtc : null,
                    quote.Lines,
                    quote.TotalCents,
                    quote.Note,
                    quote.AnswerBy,
                    RequestUrlOf(supplier, request)),
                EmailTemplates.Names.ServiceRequestQuoteReceived);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Customer e-mail for the quote on showcase request {Id} could not be queued", request.Id);
        }
    }

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

    /// <summary>
    /// The customer cancelled its request: the supplier is told (e-mail and push, in Italian, comune and "Nome C." only, the reason
    /// the customer wrote, and — for a request the supplier had taken — that the notice was short when it was), and the customer
    /// gets the receipt in its language. The two are independent: a failure of one does not stop the other. Never throws.
    /// </summary>
    public async Task NotifyCancelledByCustomerAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        SupplierContact? supplier;
        ServiceCustomerContact? contact;
        try
        {
            supplier = await TryFindSupplierAsync(request, cancellationToken);
            contact = await TryFindCustomerAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The parties of the showcase request {Id} cancelled by its customer could not be read", request.Id);
            return;
        }

        try
        {
            if (supplier is not null)
            {
                var italian = EmailTemplates.DefaultCulture;
                var comune = request.LocationCity ?? string.Empty;
                if (request.Kind == ServiceRequestKind.Custom)
                {
                    emailQueue.Enqueue(
                        supplier.Email,
                        EmailTemplates.SupplierInquiryClosedByCustomer(
                            italian,
                            supplier.LegalName,
                            comune,
                            ShortNameOf(contact),
                            TextOfReason(request.CancellationReason),
                            publicSiteLinks.SupplierInbox()),
                        EmailTemplates.Names.SupplierInquiryClosedByCustomer);
                }
                else
                {
                    emailQueue.Enqueue(
                        supplier.Email,
                        EmailTemplates.SupplierBookingCancelledByCustomer(
                            italian,
                            supplier.LegalName,
                            ServiceNameOf(request, italian),
                            comune,
                            ShortNameOf(contact),
                            request.ScheduledStartUtc ?? request.CreatedAt,
                            TextOfReason(request.CancellationReason),
                            ShortNoticeHours(request),
                            publicSiteLinks.SupplierInbox()),
                        EmailTemplates.Names.SupplierBookingCancelledByCustomer);
                }

                var push = EmailTemplates.ShowcaseCancelledByCustomerPush(italian, request.Category, comune);
                pushNotifications.Enqueue(
                    PushDeliveryKeys.ServiceRequestStatus(request.Id, ServiceRequestStatus.Annullato),
                    PushAudience.SupplierOrg(request.SupplierOrgId),
                    new PushNotificationPayload(
                        push.Title, push.Body, PushTypes.ServiceRequestCancelled, BookingId: null, PushRoutes.SupplierRequest(request.Id), request.Id));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Supplier notification for the showcase request {Id} cancelled by its customer could not be queued", request.Id);
        }

        try
        {
            if (supplier is not null && contact is not null && request.Kind != ServiceRequestKind.Custom)
            {
                var culture = EmailTemplates.CultureOf(contact.Locale);
                emailQueue.Enqueue(
                    contact.Email,
                    EmailTemplates.SupplierBookingCancellationReceipt(
                        culture,
                        contact.FullName,
                        supplier.LegalName,
                        ServiceNameOf(request, culture),
                        request.ScheduledStartUtc ?? request.CreatedAt,
                        request.PublicCode ?? string.Empty,
                        TextOfReason(request.CancellationReason),
                        SupplierPageOf(supplier)),
                    EmailTemplates.Names.SupplierBookingCancellationReceipt);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Cancellation receipt for the showcase request {Id} could not be queued", request.Id);
        }
    }

    public async Task NotifyRescheduledByCustomerAsync(
        ServiceRequest request,
        DateTime previousStartUtc,
        bool proposalDropped,
        CancellationToken cancellationToken)
    {
        if (request is not { ScheduledStartUtc: { } newStart, ResponseDueAt: { } respondBy })
            return;

        try
        {
            var supplier = await TryFindSupplierAsync(request, cancellationToken);
            if (supplier is null)
                return;

            var contact = await TryFindCustomerAsync(request, cancellationToken);
            var italian = EmailTemplates.DefaultCulture;
            var comune = request.LocationCity ?? string.Empty;
            emailQueue.Enqueue(
                supplier.Email,
                EmailTemplates.SupplierBookingRescheduledByCustomer(
                    italian,
                    supplier.LegalName,
                    ServiceNameOf(request, italian),
                    comune,
                    ShortNameOf(contact),
                    previousStartUtc,
                    newStart,
                    respondBy,
                    proposalDropped,
                    publicSiteLinks.SupplierInbox()),
                EmailTemplates.Names.SupplierBookingRescheduledByCustomer);

            var push = EmailTemplates.ShowcaseRescheduledByCustomerPush(italian, request.Category, comune);
            pushNotifications.Enqueue(
                PushDeliveryKeys.ServiceRequestRescheduled(request.Id, request.UpdatedAt),
                PushAudience.SupplierOrg(request.SupplierOrgId),
                new PushNotificationPayload(
                    push.Title, push.Body, PushTypes.ServiceRequestRescheduled, BookingId: null, PushRoutes.SupplierRequest(request.Id), request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Supplier notification for the showcase request {Id} moved by its customer could not be queued", request.Id);
        }
    }

    /// <summary>
    /// The customer accepted or turned down the time the supplier proposed: the supplier is told (e-mail and push), and — when the
    /// customer accepted, which takes the request — the customer gets the same e-mail as when the supplier takes a request, with
    /// the new time. <paramref name="proposedAt"/> is the instant of the proposal that was answered (the request does not keep it
    /// anymore). Never throws.
    /// </summary>
    public async Task NotifyProposalAnsweredByCustomerAsync(
        ServiceRequest request,
        bool accepted,
        DateTime proposedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var supplier = await TryFindSupplierAsync(request, cancellationToken);
            if (supplier is not null)
            {
                var contact = await TryFindCustomerAsync(request, cancellationToken);
                var italian = EmailTemplates.DefaultCulture;
                var comune = request.LocationCity ?? string.Empty;
                emailQueue.Enqueue(
                    supplier.Email,
                    EmailTemplates.SupplierBookingProposalAnsweredByCustomer(
                        italian,
                        supplier.LegalName,
                        ServiceNameOf(request, italian),
                        comune,
                        ShortNameOf(contact),
                        accepted,
                        request.ScheduledStartUtc ?? request.CreatedAt,
                        request.ResponseDueAt,
                        publicSiteLinks.SupplierInbox()),
                    EmailTemplates.Names.SupplierBookingProposalAnsweredByCustomer);

                var push = EmailTemplates.ShowcaseProposalAnsweredPush(italian, request.Category, comune, accepted);
                pushNotifications.Enqueue(
                    PushDeliveryKeys.ServiceRequestProposalAnswered(request.Id, proposedAt, accepted),
                    PushAudience.SupplierOrg(request.SupplierOrgId),
                    new PushNotificationPayload(
                        push.Title,
                        push.Body,
                        accepted ? PushTypes.ServiceRequestProposalAccepted : PushTypes.ServiceRequestProposalRejected,
                        BookingId: null,
                        PushRoutes.SupplierRequest(request.Id),
                        request.Id));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Supplier notification for the answer of the customer to the time proposed for {Id} could not be queued", request.Id);
        }

        if (accepted)
            await NotifyCustomerOfStatusAsync(request, cancellationToken);
    }

    /// <summary>
    /// The customer accepted or turned down the quote (SP-04b): the supplier is told (e-mail and push, in Italian, comune and "Nome C." only)
    /// and — when the customer accepted, which takes the request — the customer gets the same e-mail as when the supplier takes a request,
    /// with the price of the quote. <paramref name="quote"/> is the quote that was answered (the request does not keep it anymore). Never throws.
    /// </summary>
    public async Task NotifyQuoteAnsweredByCustomerAsync(
        ServiceRequest request,
        bool accepted,
        ServiceRequestQuote quote,
        CancellationToken cancellationToken)
    {
        try
        {
            var supplier = await TryFindSupplierAsync(request, cancellationToken);
            if (supplier is not null)
            {
                var contact = await TryFindCustomerAsync(request, cancellationToken);
                var italian = EmailTemplates.DefaultCulture;
                var comune = request.LocationCity ?? string.Empty;
                emailQueue.Enqueue(
                    supplier.Email,
                    EmailTemplates.ServiceRequestQuoteAnswered(
                        italian,
                        supplier.LegalName,
                        ServiceNameOf(request, italian),
                        comune,
                        ShortNameOf(contact),
                        accepted,
                        quote.TotalCents,
                        accepted ? request.ScheduledStartUtc : null,
                        publicSiteLinks.SupplierInbox()),
                    EmailTemplates.Names.ServiceRequestQuoteAnswered);

                var push = EmailTemplates.ServiceRequestQuoteAnsweredPush(italian, request.Category, comune, accepted);
                pushNotifications.Enqueue(
                    PushDeliveryKeys.ServiceRequestQuote(request.Id, quote.SentAt, accepted ? "accepted" : "rejected"),
                    PushAudience.SupplierOrg(request.SupplierOrgId),
                    new PushNotificationPayload(
                        push.Title,
                        push.Body,
                        accepted ? PushTypes.ServiceRequestQuoteAccepted : PushTypes.ServiceRequestQuoteRejected,
                        BookingId: null,
                        PushRoutes.SupplierRequest(request.Id),
                        request.Id));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Supplier notification for the answer of the customer to the quote of {Id} could not be queued", request.Id);
        }

        if (accepted)
            await NotifyCustomerOfStatusAsync(request, cancellationToken);
    }

    public async Task NotifySupplierOfLapsedQuoteAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var supplier = await TryFindSupplierAsync(request, cancellationToken);
            if (supplier is null)
                return;

            var contact = await TryFindCustomerAsync(request, cancellationToken);
            var italian = EmailTemplates.DefaultCulture;
            var comune = request.LocationCity ?? string.Empty;
            emailQueue.Enqueue(
                supplier.Email,
                EmailTemplates.SupplierBookingQuoteLapsed(
                    italian,
                    supplier.LegalName,
                    ServiceNameOf(request, italian),
                    comune,
                    ShortNameOf(contact),
                    publicSiteLinks.SupplierInbox()),
                EmailTemplates.Names.SupplierBookingQuoteLapsed);

            var push = EmailTemplates.ServiceRequestQuoteExpiredPush(italian, request.Category, comune);
            pushNotifications.Enqueue(
                PushDeliveryKeys.ServiceRequestStatus(request.Id, ServiceRequestStatus.Annullato),
                PushAudience.SupplierOrg(request.SupplierOrgId),
                new PushNotificationPayload(
                    push.Title, push.Body, PushTypes.ServiceRequestQuoteExpired, BookingId: null, PushRoutes.SupplierRequest(request.Id), request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Supplier notification for the lapsed quote of the showcase request {Id} could not be queued", request.Id);
        }
    }

    /// <summary>
    /// The customer did not answer the time the supplier proposed and the request was cancelled by CasaZen: the supplier is told
    /// (e-mail and push) that it is the customer who did not answer. The customer's own e-mail is
    /// <see cref="NotifyCustomerOfStatusAsync"/>. Never throws.
    /// </summary>
    public async Task NotifySupplierOfLapsedProposalAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var supplier = await TryFindSupplierAsync(request, cancellationToken);
            if (supplier is null)
                return;

            var contact = await TryFindCustomerAsync(request, cancellationToken);
            var italian = EmailTemplates.DefaultCulture;
            var comune = request.LocationCity ?? string.Empty;
            emailQueue.Enqueue(
                supplier.Email,
                EmailTemplates.SupplierBookingProposalLapsed(
                    italian,
                    supplier.LegalName,
                    ServiceNameOf(request, italian),
                    comune,
                    ShortNameOf(contact),
                    publicSiteLinks.SupplierInbox()),
                EmailTemplates.Names.SupplierBookingProposalLapsed);

            var push = EmailTemplates.ShowcaseProposalLapsedPush(italian, request.Category, comune);
            pushNotifications.Enqueue(
                PushDeliveryKeys.ServiceRequestStatus(request.Id, ServiceRequestStatus.Annullato),
                PushAudience.SupplierOrg(request.SupplierOrgId),
                new PushNotificationPayload(
                    push.Title, push.Body, PushTypes.ServiceRequestCancelled, BookingId: null, PushRoutes.SupplierRequest(request.Id), request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Supplier notification for the lapsed proposal of the showcase request {Id} could not be queued", request.Id);
        }
    }

    /// <summary>
    /// A request on measure that nobody answered yet: the supplier is reminded by e-mail and push (Italian, the comune and "Nome C." only, the time to
    /// answer by, never the words of the customer) — at half of the time it has, and ten minutes before the deadline when <paramref name="lastCall"/>
    /// is true. An operational notice like the reminder of a host: no switch of «Notifiche» hides it, because it is the supplier that has to act (the
    /// request closes otherwise, and the missed answer weighs on its reliability). Called after the mark of the reminder is saved, by the winner only.
    /// Never throws.
    /// </summary>
    public async Task NotifySupplierOfReplyReminderAsync(ServiceRequest request, bool lastCall, CancellationToken cancellationToken)
    {
        try
        {
            if (request.ResponseDueAt is not { } respondBy)
                return;

            var supplier = await TryFindSupplierAsync(request, cancellationToken);
            if (supplier is null)
                return;

            var contact = await TryFindCustomerAsync(request, cancellationToken);
            var italian = EmailTemplates.DefaultCulture;
            var comune = request.LocationCity ?? string.Empty;
            emailQueue.Enqueue(
                supplier.Email,
                EmailTemplates.SupplierInquiryReplyReminder(
                    italian, supplier.LegalName, comune, ShortNameOf(contact), respondBy, lastCall, publicSiteLinks.SupplierInbox()),
                EmailTemplates.Names.SupplierInquiryReplyReminder);

            var push = EmailTemplates.ShowcaseReplyReminderPush(italian, request.Category, comune, respondBy, lastCall);
            pushNotifications.Enqueue(
                PushDeliveryKeys.ServiceRequestReplyReminder(request.Id, lastCall ? "last" : "half"),
                PushAudience.SupplierOrg(request.SupplierOrgId),
                new PushNotificationPayload(
                    push.Title, push.Body, PushTypes.ServiceRequestReplyReminder, BookingId: null, PushRoutes.SupplierRequest(request.Id), request.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reminder to answer the showcase request on measure {Id} could not be queued", request.Id);
        }
    }

    /// <summary>
    /// What the customer of a request on measure is told when the status changes by the act of the supplier or of CasaZen: the request was taken
    /// (the customer accepted the offer: the same mail as a booking the supplier took, with the time and the price of the offer), refused, cancelled
    /// by the supplier (after it had been taken, or before), or closed because the supplier did not answer in time or because the customer did not
    /// answer the offer in time (each told the truth: decision D24).
    /// </summary>
    private void QueueInquiryStatus(
        ServiceRequest request,
        ServiceCustomerContact contact,
        SupplierContact supplier,
        System.Globalization.CultureInfo culture,
        IReadOnlyList<EmailTemplates.EmailAlternative>? alternativeLinks)
    {
        switch (request.Status)
        {
            case ServiceRequestStatus.PresoInCarico when request.ScheduledStartUtc is { } start:
                emailQueue.Enqueue(
                    contact.Email,
                    EmailTemplates.SupplierBookingAccepted(
                        culture,
                        contact.FullName,
                        supplier.LegalName,
                        ServiceNameOf(request, culture),
                        start,
                        request.QuotedAmountCents,
                        request.EstimatedAmountCents,
                        ServiceRequestReminderRules.WillBeSent(start, request.TakenAt ?? DateTime.MaxValue),
                        RequestUrlOf(supplier, request),
                        cardCharged: request.PaymentMode == ServiceRequestPaymentMode.Online),
                    EmailTemplates.Names.SupplierBookingAccepted);
                break;

            case ServiceRequestStatus.Rifiutato:
                emailQueue.Enqueue(
                    contact.Email,
                    EmailTemplates.SupplierInquiryDeclined(culture, contact.FullName, supplier.LegalName, request.RejectionReason, SupplierPageOf(supplier)),
                    EmailTemplates.Names.SupplierInquiryDeclined);
                break;

            case ServiceRequestStatus.Annullato when request.CancelledBy == ServiceRequestActorParty.Supplier && request.ScheduledStartUtc is { } cancelledStart:
                emailQueue.Enqueue(
                    contact.Email,
                    EmailTemplates.SupplierBookingCancelled(
                        culture, contact.FullName, supplier.LegalName, ServiceNameOf(request, culture), cancelledStart, TextOfReason(request.CancellationReason), SupplierPageOf(supplier)),
                    EmailTemplates.Names.SupplierBookingCancelled);
                break;

            case ServiceRequestStatus.Annullato when request.CancelledBy == ServiceRequestActorParty.Supplier:
                emailQueue.Enqueue(
                    contact.Email,
                    EmailTemplates.SupplierInquiryDeclined(
                        culture, contact.FullName, supplier.LegalName, TextOfReason(request.CancellationReason), SupplierPageOf(supplier)),
                    EmailTemplates.Names.SupplierInquiryDeclined);
                break;

            case ServiceRequestStatus.Annullato when request.CancelledBy == ServiceRequestActorParty.System
                                                     && request.CancellationReason == ServiceRequestCancellationReasons.QuoteNotAnswered:
                emailQueue.Enqueue(
                    contact.Email,
                    EmailTemplates.SupplierInquiryOfferExpired(culture, contact.FullName, supplier.LegalName, SupplierPageOf(supplier)),
                    EmailTemplates.Names.SupplierInquiryOfferExpired);
                break;

            case ServiceRequestStatus.Annullato when request.CancelledBy == ServiceRequestActorParty.System:
                emailQueue.Enqueue(
                    contact.Email,
                    EmailTemplates.SupplierInquiryExpired(culture, contact.FullName, supplier.LegalName, SupplierPageOf(supplier), alternativeLinks),
                    EmailTemplates.Names.SupplierInquiryExpired);
                break;
        }
    }

    public async Task NotifyOfferNotCompletedAsync(ServiceRequest request, bool timeGone, CancellationToken cancellationToken)
    {
        try
        {
            var (contact, supplier) = await FindPartiesAsync(request, cancellationToken);
            if (contact is null || supplier is null)
                return;

            emailQueue.Enqueue(
                contact.Email,
                EmailTemplates.SupplierInquiryOfferNotCompleted(
                    EmailTemplates.CultureOf(contact.Locale), contact.FullName, supplier.LegalName, timeGone, RequestUrlOf(supplier, request)),
                EmailTemplates.Names.SupplierInquiryOfferNotCompleted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Customer e-mail for the offer of the showcase request {Id} that could not be completed could not be queued", request.Id);
        }
    }

    /// <summary>
    /// A party wrote in the thread of a request on measure: the other one is told by e-mail. The customer hears of the supplier's reply, in its
    /// language, with the link to the request page; the supplier hears of the customer's message in Italian, with the comune and "Nome C."
    /// only. <b>Never the words</b>. Never throws.
    /// </summary>
    public async Task NotifyThreadMessageAsync(ServiceRequest request, ServiceRequestMessageAuthor author, CancellationToken cancellationToken)
    {
        try
        {
            if (author == ServiceRequestMessageAuthor.System)
                return;

            var (contact, supplier) = await FindPartiesAsync(request, cancellationToken);
            if (contact is null || supplier is null)
                return;

            if (author == ServiceRequestMessageAuthor.Supplier)
            {
                emailQueue.Enqueue(
                    contact.Email,
                    EmailTemplates.SupplierInquiryReply(
                        EmailTemplates.CultureOf(contact.Locale), contact.FullName, supplier.LegalName, RequestUrlOf(supplier, request)),
                    EmailTemplates.Names.SupplierInquiryReply);
                return;
            }

            emailQueue.Enqueue(
                supplier.Email,
                EmailTemplates.SupplierInquiryMessage(
                    EmailTemplates.DefaultCulture,
                    supplier.LegalName,
                    request.LocationCity ?? string.Empty,
                    ShortNameOf(contact),
                    publicSiteLinks.SupplierInbox()),
                EmailTemplates.Names.SupplierInquiryMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Notification of a message of the thread of the showcase request {Id} could not be queued", request.Id);
        }
    }

    private async Task<IReadOnlyList<ShowcaseAlternative>?> FindAlternativesAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (alternatives is null)
            return null;

        try
        {
            return await alternatives.FindAsync(
                new ShowcaseAlternativesQuery(request.SupplierOrgId, request.Category, request.LocationComuneIstat, request.LocationCity),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The alternatives for the lapsed showcase request {Id} could not be looked for", request.Id);
            return null;
        }
    }

    private IReadOnlyList<EmailTemplates.EmailAlternative>? AlternativeLinks(IReadOnlyList<ShowcaseAlternative>? found) =>
        found?.Select(alternative => new EmailTemplates.EmailAlternative(alternative, publicSiteLinks.SupplierShowcase(alternative.Slug))).ToList();

    private async Task<(ServiceCustomerContact? Contact, SupplierContact? Supplier)> FindPartiesAsync(
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        var contact = await TryFindCustomerAsync(request, cancellationToken);
        if (contact is null)
            return (null, null);

        return (contact, await TryFindSupplierAsync(request, cancellationToken));
    }

    private async Task<ServiceCustomerContact?> TryFindCustomerAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        if (request.CustomerId is not { } customerId)
            return null;

        var contact = await customers.FindContactAsync(request.SupplierOrgId, customerId, cancellationToken);
        return contact is null || contact.Anonymized || string.IsNullOrWhiteSpace(contact.Email) ? null : contact;
    }

    private Task<SupplierContact?> TryFindSupplierAsync(ServiceRequest request, CancellationToken cancellationToken) =>
        db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == request.SupplierOrgId)
            .Select(sp => new SupplierContact(sp.Email, sp.LegalName, sp.ShowcaseSlug))
            .FirstOrDefaultAsync(cancellationToken);

    private static string ServiceNameOf(ServiceRequest request, System.Globalization.CultureInfo culture) =>
        request.ServiceNameSnapshot ?? EmailTemplates.ServiceCategoryLabel(culture, request.Category);

    private static string ShortNameOf(ServiceCustomerContact? contact) =>
        contact is null ? string.Empty : ShowcaseBookingRules.AbbreviateName(contact.FullName);

    private static string? TextOfReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) || ServiceRequestCancellationReasons.IsCode(reason) ? null : reason;

    private int? ShortNoticeHours(ServiceRequest request)
    {
        var hours = options.Value.FreeCancellationHours;
        if (hours <= 0
            || request.TakenAt is null
            || request.ScheduledStartUtc is not { } start
            || request.CancelledAt is not { } cancelledAt)
        {
            return null;
        }

        return ShowcaseBookingManagementRules.IsFreeCancellation(start, cancelledAt, hours) ? null : hours;
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
