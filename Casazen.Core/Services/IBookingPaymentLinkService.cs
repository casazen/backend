using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The payment link of a booking the host entered (the payment step of the new-booking wizard, NF-74, E08/F01/US01/T05): the guest receives a
/// secure link to the checkout page of the booking site (<c>/book/{orgSlug}/booking/{id}?token=...</c>) and pays the balance with Stripe, on the
/// host's connected account, with no CasaZen fee. The page and its payment session are the ones of the public checkout
/// (<see cref="ICheckoutOutcomeService"/>); the payment is recorded by the payment webhook, without confirming or announcing the booking again.
/// </summary>
public interface IBookingPaymentLinkService
{
    /// <summary>
    /// Checks, before a booking is written, that the org can take a payment link: a connected Stripe account that takes charges, a public
    /// site address for the link and a slug.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">422 <see cref="BookingPaymentLinkErrorCodes.Unavailable"/>.</exception>
    Task EnsureAvailableAsync(Guid orgId, CancellationToken ct);

    /// <summary>
    /// Makes the link of the balance of a host booking of the caller's org and e-mails it to the guest: the PaymentIntent of the balance on the
    /// host's connected account (the open one is kept when its amount is still the balance, otherwise cancelled and replaced), a new 256-bit
    /// token whose SHA-256 replaces <see cref="Booking.CheckoutTokenHash"/> (so the previous link stops working), <see cref="Booking.PaymentLinkSentAt"/>
    /// and the arrangement <see cref="HostPaymentArrangement.PaymentLink"/>. The answer carries the link once, for the host to copy: the raw token
    /// is never stored.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist or is of another org (404).</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 <see cref="BookingPaymentLinkErrorCodes.Unavailable"/>: not a host booking, cancelled, nothing left to pay, a payment in progress, no
    /// Stripe account ready or no public site address.
    /// </exception>
    Task<BookingPaymentLink> CreateAsync(Guid bookingId, CancellationToken ct);
}

/// <summary>A payment link made by <see cref="IBookingPaymentLinkService.CreateAsync"/>.</summary>
/// <param name="BookingId">The booking.</param>
/// <param name="Url">The link, given this once (the token in it is never stored).</param>
/// <param name="Amount">The balance the guest pays.</param>
/// <param name="Currency">The currency of the property (ISO code).</param>
/// <param name="SentAt">When the link was made (UTC).</param>
/// <param name="EmailQueued">The e-mail to the guest was queued (false when the guest has no address or e-mail is not configured).</param>
public sealed record BookingPaymentLink(Guid BookingId, string Url, decimal Amount, string Currency, DateTime SentAt, bool EmailQueued);

/// <summary>The stable codes of the payment link of a host booking.</summary>
public static class BookingPaymentLinkErrorCodes
{
    /// <summary>The link cannot be made: no Stripe account ready, nothing to pay, not a host booking, cancelled, a payment in progress.</summary>
    public const string Unavailable = "payment_link_unavailable";

    /// <summary>The resource key of <see cref="Unavailable"/>.</summary>
    public const string UnavailableMessageKey = "PaymentLinkUnavailable";
}

/// <summary>
/// The rules of the payment link of a host booking (E08/F01/US01/T05), the same for the link, the checkout page and the payment webhook. Pure.
/// </summary>
public static class HostPaymentLinks
{
    /// <summary>
    /// A booking the host entered whose guest pays with a link: source <see cref="BookingSource.Manual"/> and arrangement
    /// <see cref="HostPaymentArrangement.PaymentLink"/>. Only these open the checkout page with a link of the host.
    /// </summary>
    public static bool IsHostPaymentLink(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);

        return booking.Source == BookingSource.Manual && booking.HostPaymentArrangement == HostPaymentArrangement.PaymentLink;
    }

    /// <summary>What is still to pay: the total minus what came in (refunds taken off), never below zero; the figure of "Registra incasso".</summary>
    public static decimal BalanceOf(Booking booking, IEnumerable<Payment> payments)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(payments);

        var paid = BookingListRules.PaidAmount(payments.Select(p => new BookingPaymentFact(p.Status, p.Amount, p.RefundedAmount)));
        return Math.Max(0m, booking.TotalPrice - paid);
    }

    /// <summary>The PaymentIntent of the link the guest can still pay: the newest payment with a PaymentIntent that is pending or failed.</summary>
    public static Payment? OpenLinkPayment(IEnumerable<Payment> payments)
    {
        ArgumentNullException.ThrowIfNull(payments);

        return payments
            .Where(p => p.StripePaymentIntentId != null && p.Status is PaymentStatus.Pending or PaymentStatus.Failed)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// Where the payment of a host booking stands for its guest on the checkout page: cancelled; being paid (Stripe said so); paid
    /// (<see cref="CheckoutOutcomeState.Confirmed"/>, nothing left); the last attempt failed; or waiting for the payment. Needs the payments.
    /// </summary>
    public static CheckoutOutcomeState StateOf(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);

        if (booking.Status == BookingStatus.Cancelled)
            return CheckoutOutcomeState.Cancelled;

        if (booking.Payments.Any(p => p.Status == PaymentStatus.Processing))
            return CheckoutOutcomeState.PaymentProcessing;

        if (BalanceOf(booking, booking.Payments) <= 0m)
            return CheckoutOutcomeState.Confirmed;

        return OpenLinkPayment(booking.Payments)?.Status == PaymentStatus.Failed
            ? CheckoutOutcomeState.PaymentFailed
            : CheckoutOutcomeState.AwaitingPayment;
    }
}
