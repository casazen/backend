namespace Casazen.Core.Services;

/// <summary>
/// The PaymentIntent of a payment held by CasaZen to create (SP-15c-a): on the <b>platform</b> account, never on a supplier's, and with
/// <b>no</b> application fee: the commission is what CasaZen does not transfer.
/// </summary>
/// <param name="AmountCents">What the payer pays: the whole price.</param>
/// <param name="Currency">Lowercase ISO currency.</param>
/// <param name="ManualCapture">
/// True to block the card and charge it later (<c>capture_method = manual</c>: the booking of a showcase customer, charged when the
/// supplier accepts); false to charge at once (the host who pays from a link, SP-15c-b).
/// </param>
/// <param name="TransferGroup">The Stripe <c>transfer_group</c> of the request: the intent, its refunds and its transfer carry it.</param>
/// <param name="Metadata">Ids only (payment or hold, request, supplier); the gateway adds <c>kind = service-charge</c> and <c>funds = platform-held</c> itself.</param>
/// <param name="IdempotencyKey">A retry with the same key gets the same PaymentIntent.</param>
/// <param name="Description">Plain text shown by Stripe next to the payment; no personal data.</param>
public sealed record PlatformChargeRequest(
    long AmountCents,
    string Currency,
    bool ManualCapture,
    string TransferGroup,
    IReadOnlyDictionary<string, string> Metadata,
    string IdempotencyKey,
    string? Description);

/// <summary>A PaymentIntent of the platform as the gateway reports it: only what CasaZen reads, no Stripe type.</summary>
/// <param name="Id">The PaymentIntent id (<c>pi_…</c>).</param>
/// <param name="Status">The Stripe status: <c>requires_payment_method</c>, <c>requires_action</c>, <c>requires_capture</c>, <c>succeeded</c>, <c>canceled</c>…</param>
/// <param name="AmountCents">The amount of the PaymentIntent.</param>
/// <param name="AmountCapturableCents">What can still be charged from the block (<c>amount_capturable</c>): the amount while the block stands, 0 once charged or canceled.</param>
/// <param name="AmountReceivedCents">What was actually charged (<c>amount_received</c>).</param>
/// <param name="Currency">Lowercase ISO currency.</param>
/// <param name="ApplicationFeeCents">The application fee on the PaymentIntent, which must be <c>null</c> (the webhook checks it); never set by CasaZen for a held payment.</param>
/// <param name="ClientSecret">What Stripe.js needs to confirm the payment; a credential: never logged and never stored.</param>
/// <param name="LatestChargeId">The charge (<c>ch_…</c>) of the PaymentIntent: the <c>source_transaction</c> of the transfer to the supplier.</param>
/// <param name="CaptureBefore">When the card network stops guaranteeing the block (<c>capture_before</c> of the charge), if Stripe says; informative.</param>
/// <param name="TransferGroup">The transfer group Stripe has on the PaymentIntent.</param>
/// <param name="LastErrorCode">Stripe code of the last payment error (<c>card_declined</c>…), if any.</param>
/// <param name="Metadata">What CasaZen wrote on the PaymentIntent (ids only).</param>
public sealed record PlatformCharge(
    string Id,
    string Status,
    long AmountCents,
    long AmountCapturableCents,
    long AmountReceivedCents,
    string Currency,
    long? ApplicationFeeCents,
    string? ClientSecret,
    string? LatestChargeId,
    DateTime? CaptureBefore,
    string? TransferGroup,
    string? LastErrorCode,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>A refund of a platform PaymentIntent to create: paid from CasaZen's balance, on the platform account (no connected account, no fee to give back).</summary>
/// <param name="PaymentIntentId">The PaymentIntent that was charged.</param>
/// <param name="AmountCents">The amount to give back to the payer.</param>
/// <param name="IdempotencyKey">A retry with the same key gets the same refund (<c>service-charge-refund:{payment}:{n}</c>).</param>
/// <param name="Metadata">Ids only; the gateway adds <c>kind = service-charge-refund</c> itself.</param>
public sealed record PlatformRefundRequest(
    string PaymentIntentId,
    long AmountCents,
    string IdempotencyKey,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>The transfer of the money of a held payment to the supplier's Stripe account to create.</summary>
/// <param name="AmountCents">What the supplier gets: the net, minus what was refunded.</param>
/// <param name="Currency">Lowercase ISO currency, the one of the charge.</param>
/// <param name="DestinationAccountId">The supplier's connected account (<c>acct_…</c>).</param>
/// <param name="SourceChargeId">The charge the money comes from (<c>source_transaction</c>): the transfer can be made even if the funds are not settled yet, and become the supplier's when the charge's are.</param>
/// <param name="TransferGroup">The Stripe <c>transfer_group</c> of the request.</param>
/// <param name="Metadata">Ids only; the gateway adds <c>kind = service-transfer</c> itself.</param>
/// <param name="IdempotencyKey">A retry with the same key gets the same transfer.</param>
public sealed record ServiceTransferRequest(
    long AmountCents,
    string Currency,
    string DestinationAccountId,
    string SourceChargeId,
    string TransferGroup,
    IReadOnlyDictionary<string, string> Metadata,
    string IdempotencyKey);

/// <summary>A transfer as Stripe reports it (the answer of the call, a listing, or a webhook): only what CasaZen reads.</summary>
/// <param name="Id">The transfer (<c>tr_…</c>).</param>
/// <param name="AmountCents">The amount transferred.</param>
/// <param name="Currency">Lowercase ISO currency.</param>
/// <param name="DestinationAccountId">The account that received it.</param>
/// <param name="SourceChargeId">The <c>source_transaction</c>, if the transfer has one.</param>
/// <param name="TransferGroup">The transfer group.</param>
/// <param name="Reversed">True when the whole transfer was reversed.</param>
/// <param name="AmountReversedCents">How much of it was reversed.</param>
/// <param name="Metadata">What CasaZen wrote on it; empty for a transfer made outside CasaZen.</param>
public sealed record ServiceTransfer(
    string Id,
    long AmountCents,
    string Currency,
    string? DestinationAccountId,
    string? SourceChargeId,
    string? TransferGroup,
    bool Reversed,
    long AmountReversedCents,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// The Stripe calls of the payment <b>held by CasaZen</b> (SP-15c-a, decision VT-D1; the other half of the supplier payments' gateway, next
/// to <see cref="ISupplierPaymentGateway"/> which keeps the direct charge on a supplier's account). Every call is on the <b>platform</b>
/// account: there is no <c>Stripe-Account</c> header, and no application fee anywhere (<c>ApplicationFeeArchitectureTests</c>). It is an
/// interface so that tests never reach Stripe. <b>Nothing in this task calls the real one:</b> the live checks of VT-D22 (transfers to Italian
/// accounts, a refund after a transfer) are the user's.
/// </summary>
/// <remarks>
/// Idempotency is the safety of the money: creation, capture, cancellation, refund and transfer all carry a key, and every method that can
/// meet a state it cannot act on (a block already charged, a block expired) <b>returns the PaymentIntent as it really is</b> instead of
/// throwing, so the caller decides from the truth. Any other Stripe failure is a <c>StripeException</c>.
/// </remarks>
public interface IPlatformPaymentGateway
{
    /// <summary>
    /// Creates the PaymentIntent on the platform: <c>capture_method = manual</c> when asked, <c>payment_method_types = [card]</c> (VT-D22: a
    /// block only exists for cards), the <c>transfer_group</c>, <c>metadata.kind = service-charge</c> and the idempotency key.
    /// </summary>
    Task<PlatformCharge> CreateAsync(PlatformChargeRequest request, CancellationToken cancellationToken = default);

    /// <summary>The PaymentIntent as it is now on Stripe, with its charge. A missing PaymentIntent is a <c>StripeException</c> (<c>resource_missing</c>).</summary>
    Task<PlatformCharge> GetAsync(string paymentIntentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Charges the whole block. A PaymentIntent that cannot be charged any more (already charged, canceled, expired) is returned as it is,
    /// not an error: the caller sees the truth and the same key never charges twice.
    /// </summary>
    Task<PlatformCharge> CaptureAsync(string paymentIntentId, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the block (or drops an unpaid PaymentIntent) and returns the PaymentIntent as it is afterwards. A PaymentIntent that cannot
    /// be canceled any more (it was charged meanwhile) is returned as it is: the caller then gives the money back with a refund.
    /// </summary>
    Task<PlatformCharge> CancelAsync(string paymentIntentId, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Gives money back to the payer from CasaZen's balance. A 4xx is a refusal (nothing was refunded); anything else leaves the outcome unknown and is repeated with the same key.</summary>
    Task<ServiceChargeRefund> CreateRefundAsync(PlatformRefundRequest request, CancellationToken cancellationToken = default);

    /// <summary>Every refund Stripe has for a PaymentIntent of the platform.</summary>
    Task<IReadOnlyList<ServiceChargeRefund>> ListRefundsAsync(string paymentIntentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transfers money to a supplier's account with <c>source_transaction</c> and the request's <c>transfer_group</c>. Stripe never retries
    /// a failed transfer: the release job does, with its own keys. A refusal is a <c>StripeException</c>.
    /// </summary>
    Task<ServiceTransfer> CreateTransferAsync(ServiceTransferRequest request, CancellationToken cancellationToken = default);

    /// <summary>The transfers of a transfer group: how the release finds out that a transfer exists although the answer that created it never came back.</summary>
    Task<IReadOnlyList<ServiceTransfer>> ListTransfersAsync(string transferGroup, CancellationToken cancellationToken = default);
}
