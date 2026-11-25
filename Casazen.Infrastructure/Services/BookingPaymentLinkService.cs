using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IBookingPaymentLinkService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Once, in order.</b> The link is made under the booking's payment lock (<c>BookingPayment</c>, the one of "Registra incasso"): the
/// balance, the PaymentIntent and the token are decided one request at a time, so a double click never opens two payments of the same balance
/// and a receipt recorded at the same time is counted.</item>
/// <item><b>One payable PaymentIntent.</b> The open PaymentIntent of the previous link is kept when its amount is still the balance (only the token
/// changes) and cancelled on Stripe otherwise; one that is being paid or was paid stops the new link (the webhook records it). The new one is a
/// direct charge on the host's connected account with no application fee (bookings carry no CasaZen fee), created with an idempotency key
/// and the metadata of the checkout (<c>kind = direct-booking</c>), so the payment webhook settles it like a checkout payment: on a confirmed
/// booking it only records the payment as completed (<see cref="CheckoutPaymentSettlementService"/>).</item>
/// <item><b>The token</b>: 256 random bits in the link, only its SHA-256 in <see cref="Booking.CheckoutTokenHash"/>: the previous link stops
/// working, and a token opens only the booking it was made for.</item>
/// <item><b>The e-mail</b> to the guest (<see cref="EmailTemplates.BookingPaymentLink"/>, Italian and English texts, every value HTML-encoded) is
/// queued on Hangfire after the commit; the logs carry ids only.</item>
/// </list>
/// </remarks>
public sealed class BookingPaymentLinkService(
    AppDbContext db,
    IStripeService stripeService,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    ILogger<BookingPaymentLinkService> logger,
    TimeProvider? timeProvider = null,
    IGuestEmailBrandingProvider? emailBranding = null,
    IGuestEmailLanguageProvider? emailLanguage = null) : IBookingPaymentLinkService
{
    /// <summary>The description of the PaymentIntent and of the payment row of a link (no personal data).</summary>
    internal const string PaymentDescription = "Payment link of a host booking";
    internal const string GuestPaymentDescription = "Guest portal stay balance";

    /// <summary>The <c>kind</c> of the metadata of the checkout's PaymentIntents: the Connect webhook settles only these.</summary>
    internal const string CheckoutPaymentKind = "direct-booking";

    /// <summary>PaymentIntent statuses the guest can still pay.</summary>
    private static readonly HashSet<string> PayableIntentStatuses = new(StringComparer.Ordinal)
    {
        "requires_payment_method",
        "requires_confirmation",
        "requires_action",
    };

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task EnsureAvailableAsync(Guid orgId, CancellationToken ct)
    {
        var org = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new { o.Slug, o.StripeConnectedAccountId, o.ConnectChargesEnabled })
            .FirstOrDefaultAsync(ct);
        if (org is null
            || !Org.AcceptsDirectPayments(org.StripeConnectedAccountId, org.ConnectChargesEnabled)
            || string.IsNullOrWhiteSpace(org.Slug)
            || !links.IsConfigured)
        {
            throw Unavailable();
        }
    }

    public async Task<BookingPaymentLink> CreateAsync(Guid bookingId, CancellationToken ct)
    {
        // Through the tenant filter of the caller: a booking of another org is not found (404), whatever its id.
        if (!await db.Bookings.AsNoTracking().AnyAsync(b => b.Id == bookingId, ct))
            throw NotFound();

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.BookingPayment, bookingId.ToString("N")));

        var booking = await db.Bookings
            .Include(b => b.Property)
            .Include(b => b.Org)
            .Include(b => b.Guest)
            .Include(b => b.Payments)
            .FirstOrDefaultAsync(b => b.Id == bookingId, ct)
            ?? throw NotFound();

        // A booking the host entered, or a confirmed one of the booking site whose price an edit raised (E08/F02/US01/T01): a hold of the booking
        // site has its own checkout, and a portal collects its own bookings.
        var linkable = booking.Source == BookingSource.Manual
                       || (booking.Source == BookingSource.Direct && booking.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn);
        if (!linkable || booking.Status == BookingStatus.Cancelled)
            throw Unavailable();

        // A booking of the booking site paid at its deadline ("Paga alla scadenza") whose card has not been charged yet is collected by the deadline
        // charge, on the whole total, and that charge knows nothing of a link: the guest would pay twice. No link until the deadline charge is
        // collected; after it, only a price raised by an edit leaves a balance (and that one is charged to the saved card).
        if (booking.Source == BookingSource.Direct && booking.PaymentOption == PaymentOption.OnCancellationDeadline)
        {
            var deferred = DeferredCharges.FindPayment(booking, booking.Payments);
            if (deferred is null || !DeferredCharges.IsCollected(deferred.Status))
                throw Unavailable();
        }

        var org = booking.Org;
        if (!org.CanTakeDirectPayments || string.IsNullOrWhiteSpace(org.Slug) || !links.IsConfigured)
            throw Unavailable();

        // A payment Stripe is processing (a SEPA debit) is money on its way: no second link next to it.
        if (booking.Payments.Any(p => p.Status == PaymentStatus.Processing))
            throw Unavailable();

        var balance = HostPaymentLinks.BalanceOf(booking, booking.Payments);
        if (balance <= 0m)
            throw Unavailable();

        var currency = StayCurrency.ForProperty(booking.Property);
        var now = _clock.GetUtcNow().UtcDateTime;
        var prepared = await PrepareIntentAsync(booking, balance, now, fromGuestPortal: false, ct);

        // A new token: the previous link stops working, and only the hash is kept.
        var token = CheckoutOutcomes.NewToken();
        booking.CheckoutTokenHash = CheckoutOutcomes.HashToken(token);
        booking.PaymentLinkSentAt = now;
        booking.HostPaymentArrangement = HostPaymentArrangement.PaymentLink;
        booking.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        var url = links.CheckoutOutcome(org.Slug!, booking.Id, token);
        var queued = await QueueEmailAsync(booking, balance, currency, url);
        logger.LogInformation(
            "Payment link of booking {BookingId} made on payment {PaymentId} (e-mail queued: {Queued})", booking.Id, prepared.Payment.Id, queued);
        return new BookingPaymentLink(booking.Id, url, balance, currency, now, queued);
    }

    public async Task<GuestStayBalance?> ReadGuestBalanceAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        var booking = await ForGuestStay(orgId, bookingId).AsNoTracking().SingleOrDefaultAsync(ct);
        return booking is null ? null : await GuestBalanceAsync(booking, ct);
    }

    public async Task<GuestStayBalanceIntent> PrepareGuestBalanceAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct,
            (PostgresAdvisoryLocks.Scope.BookingCancellation, bookingId.ToString("N")),
            (PostgresAdvisoryLocks.Scope.BookingPayment, bookingId.ToString("N")));
        var booking = await ForGuestStay(orgId, bookingId).SingleOrDefaultAsync(ct) ?? throw NotFound();
        var balance = await GuestBalanceAsync(booking, ct);
        if (!balance.CanPay)
            throw Unavailable();

        var prepared = await PrepareIntentAsync(booking, balance.Amount, _clock.GetUtcNow().UtcDateTime, fromGuestPortal: true, ct);
        if (string.IsNullOrWhiteSpace(prepared.Intent.ClientSecret))
            throw Unavailable();
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return new GuestStayBalanceIntent(prepared.Intent.ClientSecret, booking.Org.StripeConnectedAccountId!, balance.Amount);
    }

    private IQueryable<Booking> ForGuestStay(Guid orgId, Guid bookingId) =>
        db.Bookings.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(b => b.OrgId == orgId && b.Id == bookingId)
            .Include(b => b.Property)
            .Include(b => b.Org)
            .Include(b => b.Payments);

    private async Task<GuestStayBalance> GuestBalanceAsync(Booking booking, CancellationToken ct)
    {
        var currency = StayCurrency.ForProperty(booking.Property);
        if (booking.Source != BookingSource.Direct || booking.Status == BookingStatus.Cancelled)
            return new GuestStayBalance(0m, 0m, currency, false, false, false);

        // The Octorate booking engine also maps to Direct, but CasaZen never collects an imported stay.
        if (!string.IsNullOrWhiteSpace(booking.OctorateReservationId)
            && !await db.OctorateBookingSyncs.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AnyAsync(s => s.BookingId == booking.Id && s.OrgId == booking.OrgId, ct))
            return new GuestStayBalance(0m, 0m, currency, false, false, false, External: true);

        var amount = HostPaymentLinks.BalanceOf(booking, booking.Payments);
        var deferredPayment = DeferredCharges.FindPayment(booking, booking.Payments);
        var deferred = amount > 0m && booking.PaymentOption == PaymentOption.OnCancellationDeadline
            && (deferredPayment is null || !DeferredCharges.IsCollected(deferredPayment.Status));
        var processing = amount > 0m && booking.Payments.Any(p => p.Status == PaymentStatus.Processing);
        // On-site bookings keep their agreed collection method and the separately settled tourist tax.
        var canPay = amount > 0m && !deferred && !processing && booking.Org.CanTakeDirectPayments
            && booking.PaymentOption != PaymentOption.OnSite
            && booking.NoShowAt is null && booking.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn;
        return new GuestStayBalance(amount, booking.TotalPrice, currency, canPay, deferred, processing);
    }

    private async Task<(Payment Payment, Stripe.PaymentIntent Intent)> PrepareIntentAsync(
        Booking booking, decimal balance, DateTime now, bool fromGuestPortal, CancellationToken ct)
    {
        var account = booking.Org.StripeConnectedAccountId!;
        var currency = StayCurrency.ForProperty(booking.Property);
        var amountCents = (long)Math.Round(balance * 100m, MidpointRounding.AwayFromZero);

        var open = await KeepOrReplaceOpenIntentAsync(booking, account, amountCents, now, ct);
        if (open is { } existing)
        {
            if (fromGuestPortal && existing.Payment.Description != PaymentDescription)
                existing.Payment.Description = GuestPaymentDescription;
            return existing;
        }

        var description = fromGuestPortal ? GuestPaymentDescription : PaymentDescription;
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            Amount = balance,
            Status = PaymentStatus.Pending,
            Method = PaymentMethod.CreditCard,
            StripeAccountId = account,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Stripe.PaymentIntent intent;
        try
        {
            intent = await stripeService.CreateConnectedAccountPaymentIntentAsync(
                account,
                amountCents,
                StayCurrency.ForStripe(currency),
                MetadataOf(booking, fromGuestPortal),
                $"payment-link:{booking.Id:N}:{payment.Id:N}",
                description,
                ct);
        }
        catch (Stripe.StripeException ex)
        {
            logger.LogError(ex, "Payment link of booking {BookingId}: Stripe did not create the PaymentIntent", booking.Id);
            throw new PaymentProcessingException("Payment link initialization failed", ex);
        }

        payment.TransactionId = intent.Id;
        payment.StripePaymentIntentId = intent.Id;
        db.Payments.Add(payment);
        return (payment, intent);
    }

    /// <summary>
    /// The open PaymentIntent of a previous link that is still payable for exactly <paramref name="amountCents"/> on <paramref name="account"/>
    /// (kept, back to pending); every other open one is cancelled on Stripe. <c>null</c> when none is kept.
    /// </summary>
    /// <exception cref="DomainRuleException">A PaymentIntent of the booking is being paid or was paid: the webhook records it.</exception>
    private async Task<(Payment Payment, Stripe.PaymentIntent Intent)?> KeepOrReplaceOpenIntentAsync(
        Booking booking,
        string account,
        long amountCents,
        DateTime now,
        CancellationToken ct)
    {
        (Payment Payment, Stripe.PaymentIntent Intent)? kept = null;
        var open = booking.Payments
            .Where(p => p.StripePaymentIntentId != null && p.Status is PaymentStatus.Pending or PaymentStatus.Failed)
            .OrderByDescending(p => p.CreatedAt)
            .ToList();
        foreach (var payment in open)
        {
            var intentAccount = payment.StripeAccountId ?? account;
            var intent = await stripeService.GetPaymentIntentAsync(payment.StripePaymentIntentId!, intentAccount, ct);
            if (intent.Status is "succeeded" or "processing" or "requires_capture")
                throw Unavailable();

            if (kept is null
                && intent.Status is not null
                && PayableIntentStatuses.Contains(intent.Status)
                && intent.Amount == amountCents
                && string.Equals(intentAccount, account, StringComparison.Ordinal))
            {
                payment.Status = PaymentStatus.Pending;
                payment.UpdatedAt = now;
                kept = (payment, intent);
                continue;
            }

            if (intent.Status != "canceled")
            {
                await stripeService.CancelPaymentIntentAsync(
                    payment.StripePaymentIntentId!, intentAccount, $"payment-link-replace:{booking.Id:N}:{payment.StripePaymentIntentId}", ct);
            }

            payment.Status = PaymentStatus.Canceled;
            payment.UpdatedAt = now;
        }

        return kept;
    }

    private async Task<bool> QueueEmailAsync(Booking booking, decimal amount, string currency, string url)
    {
        var to = booking.Guest?.Email?.Trim();
        if (string.IsNullOrEmpty(to))
        {
            logger.LogWarning("Payment link of booking {BookingId} not e-mailed: the guest has no address", booking.Id);
            return false;
        }

        // E50/F01/US03/T01: the brand of the host while the guest portal is on (null = the CasaZen e-mail). The link is committed: the e-mail is queued whatever happens to the request.
        var branding = await emailBranding.ForOrgOrNullAsync(booking.OrgId, CancellationToken.None);
        // E50/F01/US03/T02: with the guest portal on, in the language of the guest (the check-in page, the country, the account of the host); without it, in Italian as ever.
        var culture = await emailLanguage.CultureOrAsync(booking.OrgId, booking.Id, EmailTemplates.DefaultCulture, cancellationToken: CancellationToken.None);
        var content = EmailTemplates.BookingPaymentLink(
            culture,
            booking.Guest!.FirstName,
            booking.Property.Name,
            booking.CheckInDate,
            booking.CheckOutDate,
            amount,
            currency,
            url,
            branding);
        return emailQueue.Enqueue(to, content, EmailTemplates.Names.BookingPaymentLink);
    }

    private static Dictionary<string, string> MetadataOf(Booking booking, bool fromGuestPortal) => new()
    {
        ["bookingId"] = booking.Id.ToString(),
        ["propertyId"] = booking.PropertyId.ToString(),
        ["orgId"] = booking.OrgId.ToString(),
        ["kind"] = CheckoutPaymentKind,
        ["source"] = fromGuestPortal ? "guest-portal" : "host-payment-link",
    };

    private static NotFoundException NotFound() =>
        new("Booking not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

    private static DomainRuleException Unavailable() =>
        new(BookingPaymentLinkErrorCodes.Unavailable, BookingPaymentLinkErrorCodes.UnavailableMessageKey);
}
