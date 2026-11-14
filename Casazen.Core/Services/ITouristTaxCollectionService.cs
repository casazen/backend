using Casazen.Core.Entities.Enums;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

/// <summary>
/// The tourist tax paid online in the guest check-in, never twice (E25/F01/US01/T01, NF-119, decision O12): what is due (<see cref="TouristTaxCollectionRules"/>),
/// one PaymentIntent per stay on the organization's own connected account, the webhook that records the payment once, the refund of a stay that never had
/// a night, the host's «Pagata in struttura» and the setting of the organization. The collection lives on <c>StayTouristTax</c>: no <c>Payment</c> row is
/// ever created, so the owner ledger, the balance of the booking and the revenue do not change. Everything is behind <c>Features:TouristTaxOnlinePayment</c>.
/// </summary>
public interface ITouristTaxCollectionService
{
    /// <summary>
    /// What the guest of the check-in link <paramref name="token"/> pays (no name, no exemption reason: positions and amounts). <c>null</c> (404) for an
    /// unknown or expired link and for a stay that is not confirmed or in progress; 409 <c>tourist_tax_payment_not_available</c> while the organization does
    /// not offer the payment or its account does not take payments.
    /// </summary>
    Task<TouristTaxGuestView?> GetForGuestAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// The PaymentIntent of what the guest of <paramref name="token"/> can pay online now: the open one of the stay when its amount is still the one due,
    /// else a new one on the connected account (the previous one cancelled first), with the metadata <c>kind = tourist-tax</c> and an idempotency key of the
    /// stay and the amount. <c>null</c> (404) like <see cref="GetForGuestAsync"/>; 409 <c>tourist_tax_payment_not_available</c>, 409
    /// <c>tourist_tax_nothing_to_pay</c> when nothing can be paid online, 409 <c>tourist_tax_payment_in_progress</c> while a payment of the stay is under way.
    /// </summary>
    Task<TouristTaxPaymentSession?> CreatePaymentIntentAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// The collection block of the host's view of the tax (through the tenant filter of the caller, who was authorized on the booking).
    /// <paramref name="tax"/> is the calculation the caller already has, so it is not made twice; <c>null</c> to make it here.
    /// </summary>
    Task<TouristTaxCollectionView> GetForHostAsync(Guid bookingId, StayTouristTaxView? tax, CancellationToken ct = default);

    /// <summary>
    /// The host records that the guest paid the tax at the property («Pagata in struttura»): an open PaymentIntent of the stay is cancelled, so the guest
    /// can no longer pay it online too. Idempotent. 422 <c>tourist_tax_collection_not_applicable</c> for a stay that is not active or whose channel collects
    /// the tax, 409 <c>tourist_tax_paid_online</c> when the guest already paid online, 409 <c>tourist_tax_payment_in_progress</c> while a payment is under way.
    /// </summary>
    Task<TouristTaxCollectionView> RecordPaidAtPropertyAsync(Guid bookingId, string userId, CancellationToken ct = default);

    /// <summary>The setting <c>TouristTaxOnlinePayment</c> of the organization and whether its account takes payments.</summary>
    Task<TouristTaxSettingsView?> GetSettingsAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>Saves the setting <c>TouristTaxOnlinePayment</c> of the organization; <c>null</c> when the organization does not exist.</summary>
    Task<TouristTaxSettingsView?> SaveSettingsAsync(
        Guid orgId,
        bool enabled,
        bool requiredBeforeAccessCode,
        string userId,
        CancellationToken ct = default);

    /// <summary>
    /// <c>payment_intent.succeeded</c> of an intent with <c>kind = tourist-tax</c>, in the transaction of the event: the stay is paid online with the amount
    /// and the currency of the intent (never a figure of the client), once. Only the intent the stay asked for, on the account it was created on, is
    /// recorded; any other is logged and left. Returns what the hosts are told once the event is committed (<see cref="CompletePaidAsync"/>), <c>null</c>
    /// when nothing was recorded now.
    /// </summary>
    Task<TouristTaxPaidNotice?> ApplyPaymentSucceededAsync(TouristTaxIntentEvent intentEvent, CancellationToken ct = default);

    /// <summary>
    /// <c>charge.refunded</c>: when the charge is the one of a stay's tax, the refunded amount of the stay follows what the charge says (never more than
    /// what was paid) and <c>true</c> is returned; <c>false</c> for a charge that is not a tourist tax's. A refund of a payment not recorded yet throws
    /// <see cref="TouristTaxCollectionPendingException"/>: the event is delivered again.
    /// </summary>
    Task<bool> ApplyChargeRefundedAsync(TouristTaxChargeRefund refund, CancellationToken ct = default);

    /// <summary>
    /// After the event of a payment is committed: the hosts of the booking are told (the bell, the pop-up and the open pages, through the push), and a
    /// difference owed back raises the host's «To do». Never throws: what fails is logged with ids.
    /// </summary>
    Task CompletePaidAsync(TouristTaxPaidNotice notice, CancellationToken ct = default);

    /// <summary>
    /// A booking was cancelled or marked a no-show (the outbox consumer): an open PaymentIntent is cancelled; when no night was spent the tax is not due
    /// and what was paid online is refunded in full on the connected account; a stay cancelled after the arrival with the tax paid online raises the
    /// host's «To do». An intent being paid throws <see cref="TouristTaxCollectionPendingException"/> (the event is delivered again); a failure of the
    /// payment provider that may pass is thrown again, one that will not raises the «To do» with its code.
    /// </summary>
    Task ApplyStayEndedAsync(Guid bookingId, DateTime endedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// The collection of the stay as it is now (the outbox consumer of the check-in, the check-out and a change of the booking): the record keeps the
    /// effective state, and a difference owed back to the guest raises the host's «To do» once.
    /// </summary>
    Task ReconcileAsync(Guid bookingId, CancellationToken ct = default);
}

/// <summary>The markers of the PaymentIntent of a tourist tax (its metadata) and the problem codes of the collection.</summary>
public static class TouristTaxCharges
{
    /// <summary>The <c>kind</c> of the metadata: the webhook routes the intent to the collection.</summary>
    public const string Kind = "tourist-tax";

    public const string KindMetadataKey = "kind";
    public const string BookingMetadataKey = "bookingId";
    public const string RecordMetadataKey = "stayTouristTaxId";
    public const string OrgMetadataKey = "orgId";
    public const string PropertyMetadataKey = "propertyId";

    /// <summary>The description of the PaymentIntent (no personal data).</summary>
    public const string PaymentDescription = "Tourist tax of a stay";

    public const string NotAvailableCode = "tourist_tax_payment_not_available";
    public const string NothingToPayCode = "tourist_tax_nothing_to_pay";
    public const string PaymentInProgressCode = "tourist_tax_payment_in_progress";
    public const string PaidOnlineCode = "tourist_tax_paid_online";
    public const string NotApplicableCode = "tourist_tax_collection_not_applicable";
}

/// <summary>The tax of one guest as the guest page shows it: a position of the stay, never a person, and never the reason of an exemption.</summary>
public sealed record TouristTaxGuestLine(int Position, int TaxableNights, decimal? Amount, bool Exempt);

/// <summary>What the guest of a check-in link sees of the tourist tax of the stay.</summary>
/// <param name="Status">The effective state of the collection.</param>
/// <param name="Reason">Why nothing can be paid online now.</param>
/// <param name="Payable">What the guest can pay online now (0 when nothing).</param>
/// <param name="AmountDue">The tax of the stay the host collects; <c>null</c> when it cannot be calculated (the host tells the amount).</param>
/// <param name="CollectedAmount">What the guest already paid online.</param>
/// <param name="Currency">The currency of the property.</param>
/// <param name="Guests">The tax guest by guest; empty when it cannot be calculated.</param>
/// <param name="RequiredBeforeAccessCode">The organization asks for the tax before the lock code.</param>
public sealed record TouristTaxGuestView(
    TouristTaxCollectionStatus Status,
    TouristTaxNotPayableReason Reason,
    decimal Payable,
    decimal? AmountDue,
    decimal CollectedAmount,
    string Currency,
    IReadOnlyList<TouristTaxGuestLine> Guests,
    bool RequiredBeforeAccessCode);

/// <summary>What the Payment Element of the guest page needs: the intent on the connected account, the platform's publishable key and the account.</summary>
public sealed record TouristTaxPaymentSession(
    string PaymentIntentId,
    string ClientSecret,
    string PublishableKey,
    string StripeAccountId,
    decimal Amount,
    string Currency);

/// <summary>The collection of a stay's tax as the host sees it.</summary>
/// <param name="Status">The effective state.</param>
/// <param name="Reason">Why the guest cannot pay online now.</param>
/// <param name="ToCollect">What is still to collect for the stay.</param>
/// <param name="OnlinePayable">What the guest can pay online now.</param>
/// <param name="RefundDue">What is owed back to the guest (the «To do»).</param>
/// <param name="AmountDue">The tax the host collects for the stay as it is now; <c>null</c> when it cannot be calculated.</param>
/// <param name="IncludedInBooking">The tax the price of the booking included.</param>
/// <param name="CollectedAmount">What the guest paid online.</param>
/// <param name="RefundedAmount">What was refunded of it.</param>
/// <param name="Currency">The currency of the collection.</param>
/// <param name="CollectedAt">When it was paid online or recorded as paid at the property.</param>
/// <param name="OnlinePaymentAvailable">The organization offers the online payment and its account takes payments.</param>
public sealed record TouristTaxCollectionView(
    TouristTaxCollectionStatus Status,
    TouristTaxNotPayableReason Reason,
    decimal ToCollect,
    decimal OnlinePayable,
    decimal RefundDue,
    decimal? AmountDue,
    decimal IncludedInBooking,
    decimal CollectedAmount,
    decimal RefundedAmount,
    string Currency,
    DateTime? CollectedAt,
    bool OnlinePaymentAvailable);

/// <summary>The setting <c>TouristTaxOnlinePayment</c> of an organization and whether its connected account takes payments.</summary>
public sealed record TouristTaxSettingsView(bool Enabled, bool RequiredBeforeAccessCode, bool PaymentsReady);

/// <summary>A <c>payment_intent.succeeded</c> of a tourist tax: what the event says, in minor units.</summary>
/// <param name="PaymentIntentId">The intent.</param>
/// <param name="AccountId">The connected account of the event (<c>account</c>); <c>null</c> for an event of the platform's own account.</param>
/// <param name="Metadata">The metadata of the intent.</param>
/// <param name="AmountReceivedMinor">What was received, in minor units of <paramref name="Currency"/>.</param>
/// <param name="Currency">The currency of the intent, as Stripe writes it.</param>
/// <param name="OccurredAtUtc">When Stripe says the event happened; <c>null</c> when it does not say.</param>
public sealed record TouristTaxIntentEvent(
    string PaymentIntentId,
    string? AccountId,
    IReadOnlyDictionary<string, string> Metadata,
    long AmountReceivedMinor,
    string? Currency,
    DateTime? OccurredAtUtc);

/// <summary>A <c>charge.refunded</c>: the intent of the charge, the account of the event and what the charge says was refunded in all, in minor units.</summary>
public sealed record TouristTaxChargeRefund(string PaymentIntentId, string? AccountId, long AmountRefundedMinor);

/// <summary>A tax paid online that was recorded: who to tell once the event is committed (ids and the language of the organization only).</summary>
public sealed record TouristTaxPaidNotice(Guid StayTouristTaxId, Guid BookingId, Guid OrgId, string PaymentIntentId, string Language);

/// <summary>
/// The state an event needs is not there yet (a payment being made, a refund of a payment not recorded yet): the event is delivered again instead of
/// being consumed with no effect.
/// </summary>
public sealed class TouristTaxCollectionPendingException(Guid bookingId)
    : Exception($"The tourist tax collection of booking {bookingId} is waiting for a payment: the event is delivered again.");
