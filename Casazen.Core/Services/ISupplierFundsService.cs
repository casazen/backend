using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>The card block of a booking hold to create (SP-15c-a): the showcase customer's card is blocked for the total they saw.</summary>
/// <param name="HoldId">The booking hold the block is for: the same hold is the same PaymentIntent (idempotency).</param>
/// <param name="SupplierOrgId">The supplier org that will be paid.</param>
/// <param name="AmountCents">The total the customer saw: what is blocked.</param>
/// <param name="Description">Plain text shown by Stripe next to the payment; no personal data.</param>
public sealed record HoldAuthorizationRequest(Guid HoldId, Guid SupplierOrgId, int AmountCents, string? Description);

/// <summary>What the payer's browser needs to block the card with the Payment Element.</summary>
/// <param name="PaymentIntentId">The PaymentIntent (<c>pi_…</c>): the hold keeps it, the webhook finds the hold by it.</param>
/// <param name="ClientSecret">A credential: sent once to the payer's browser, never logged and never stored.</param>
/// <param name="PublishableKey">The platform's publishable key.</param>
/// <param name="AmountCents">The amount that is blocked.</param>
/// <param name="Currency">Uppercase ISO currency.</param>
public sealed record HoldAuthorization(string PaymentIntentId, string ClientSecret, string PublishableKey, int AmountCents, string Currency);

/// <summary>The facts of a successful card block, from the verified webhook, that turn a hold into a request with a payment.</summary>
/// <param name="PaymentIntentId">The PaymentIntent that blocks the card.</param>
/// <param name="AmountCents">The amount blocked (equal to the amount the hold asked for: the webhook checks it).</param>
/// <param name="AuthorizedAt">When the block succeeded.</param>
/// <param name="TransferGroup">The transfer group the PaymentIntent was created with (<see cref="ServiceCharges.HoldTransferGroup"/> of the hold).</param>
/// <param name="Lines">The price lines of the work as the customer saw them.</param>
public sealed record AuthorizedPaymentInput(
    string PaymentIntentId,
    int AmountCents,
    DateTime AuthorizedAt,
    string TransferGroup,
    IReadOnlyList<ServiceRequestPriceLine> Lines);

/// <summary>What one run of the funds job (<see cref="ISupplierFundsJobService"/>) did.</summary>
/// <param name="Released">Payments transferred to the supplier.</param>
/// <param name="ReleaseFailed">Releases that did not go (the supplier's account is not ready, or Stripe refused): they are tried again later.</param>
/// <param name="ClosedSettled">Payments of closed requests whose block was released or whose money was refunded.</param>
/// <param name="AuthorizationsLapsed">Blocks that outlived <c>SupplierPayments:AuthorizationValidityHours</c> and were released.</param>
/// <param name="HoldWarnings">Admins warned about money held for nearly the maximum.</param>
/// <param name="Errors">Payments the run could not handle (logged; the next run retries them).</param>
public sealed record ServiceFundsRun(int Released, int ReleaseFailed, int ClosedSettled, int AuthorizationsLapsed, int HoldWarnings, int Errors)
{
    /// <summary>A run that found nothing to do.</summary>
    public static ServiceFundsRun Empty { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>The run did something or failed on something worth a log line.</summary>
    public bool HadWork => Released + ReleaseFailed + ClosedSettled + AuthorizationsLapsed + HoldWarnings + Errors > 0;
}

/// <summary>
/// The money CasaZen <b>holds</b> for the services of the suppliers (SP-15c-a, decision VT-D1): the card of the payer is blocked, charged when
/// the supplier accepts, kept by CasaZen, and released to the supplier a configurable time after the work is done (or when the payer says
/// "all fine"), or given back. It is the lifecycle of a <see cref="ServiceRequestPayment"/> whose <see cref="ServiceRequestPayment.FundsModel"/>
/// is <see cref="ServiceFundsModel.PlatformHeld"/>; the payments of SP-15a (a direct charge on the supplier's account) are not touched.
/// </summary>
/// <remarks>
/// <para><b>Everything is behind the flag <c>SupplierOnlinePayments</c> for the creation</b> (<see cref="CanAuthorizeAsync"/>): with it off nothing
/// changes for anybody. A block, a capture, a release or a refund that is already under way is <b>not</b> behind the flag (money in flight).</para>
/// <para><b>Concurrency.</b> Every method that changes a payment runs under the advisory lock <c>ServiceRequestPayment</c> (key: the request id) in
/// a READ COMMITTED transaction and reads the payment again after it. Stripe is called with an idempotency key for every step, and a step that
/// meets a state it cannot act on reads the real one from Stripe: the transfer is never made twice, a refund and a transfer never share the same
/// euros (<see cref="HeldPaymentRules"/>).</para>
/// </remarks>
public interface ISupplierFundsService
{
    // ─── The card of a booking, before the request exists ────────────────────────────────────────────────────────────

    /// <summary>
    /// A booking of <paramref name="amountCents"/> from <paramref name="supplierOrgId"/>'s showcase is paid inside CasaZen: the flag is on, the
    /// supplier's Stripe account takes charges and payouts, and the amount is at least <c>SupplierPayments:MinAmountCents</c>. False means the
    /// booking goes on as it always did (no payment).
    /// </summary>
    Task<bool> CanAuthorizeAsync(Guid supplierOrgId, int amountCents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the PaymentIntent that blocks the customer's card (on the platform, <c>capture_method = manual</c>, cards only, no commission) and
    /// returns what the browser needs to confirm it. The same <see cref="HoldAuthorizationRequest.HoldId"/> is the same PaymentIntent. A Stripe
    /// failure is a <c>StripeException</c> (the API answers 503 <c>payment_provider_error</c>).
    /// </summary>
    Task<HoldAuthorization> AuthorizeHoldAsync(HoldAuthorizationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The PaymentIntent of a hold read again, with its secret, for a customer who retries the same booking; null when it can no longer be
    /// paid (it was canceled, or already blocked).
    /// </summary>
    Task<HoldAuthorization?> ReadHoldAuthorizationAsync(string paymentIntentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the block of a hold that will never become a request (it lapsed, was removed, or the customer's e-mail could not be written to).
    /// Idempotent; a PaymentIntent that is gone or already canceled is not an error. A Stripe failure is a <c>StripeException</c>: the caller keeps
    /// the hold and the next run tries again.
    /// </summary>
    Task CancelHoldAuthorizationAsync(Guid holdId, string paymentIntentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the payment of a request that was just born from a hold whose card is blocked: <see cref="ServicePaymentStatus.Authorized"/>, platform
    /// funds, the commission snapshotted, the block expiring after <c>AuthorizationValidityHours</c>. The row is added to the context and <b>not
    /// saved</b>: the caller saves it with the request, in one transaction. No Stripe call. The request's payment mode is set to
    /// <see cref="ServiceRequestPaymentMode.Online"/>.
    /// </summary>
    Task<ServiceRequestPayment> AddAuthorizedPaymentAsync(
        ServiceRequest request,
        AuthorizedPaymentInput input,
        CancellationToken cancellationToken = default);

    // ─── The life of the request ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The request has a payment held by CasaZen that is not canceled: its mode is <see cref="ServiceRequestPaymentMode.Online"/> and its amount is locked.</summary>
    Task<bool> HasHeldPaymentAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Charges the customer's card because the supplier is accepting the request (<see cref="ServicePaymentStatus.Authorized"/> →
    /// <see cref="ServicePaymentStatus.Held"/>). A request with no held payment, or whose payment is already held, does nothing. Throws
    /// <c>DomainConflictException</c> <see cref="ServicePaymentErrors.AuthorizationExpired"/> when the block lapsed (it is released) and
    /// <see cref="ServicePaymentErrors.CaptureFailed"/> when the card could not be charged: the caller does <b>not</b> take the request. The
    /// same call twice charges once.
    /// </summary>
    Task CaptureForTakeAsync(ServiceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The request was refused, cancelled or lapsed: gives the payer's money back. A block is released (<see cref="ServicePaymentStatus.Canceled"/>);
    /// money that was charged is refunded in full (<see cref="ServicePaymentStatus.Refunded"/>, the commission included: decision D6). Never throws
    /// for a Stripe or a database failure (it is logged and the funds job settles it later): the closing of a request must not depend on Stripe.
    /// Idempotent; a request whose money was already released is left alone and the admins are told.
    /// </summary>
    Task SettleClosedRequestAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The payer says "all fine" (<paramref name="requestId"/> is the request of a showcase customer who proved they hold the code and the
    /// e-mail): the money is released to the supplier at once. 422 <see cref="ServicePaymentErrors.NotConfirmable"/> when there is nothing to
    /// release (the work is not completed, the payment is closed or stopped by a dispute); asking again once it is released is not an error. A
    /// transfer that cannot be made now (the supplier's account is not ready, Stripe refuses) is recorded and tried again by the job: the payer's
    /// confirmation stands.
    /// </summary>
    Task<ServiceRequestPayment> ConfirmOkAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>The live payment held by CasaZen of a request, read without tracking, or null.</summary>
    Task<ServiceRequestPayment?> FindHeldPaymentAsync(Guid requestId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The system job of the money CasaZen holds (SP-15c-a): <c>service-funds</c>, every five minutes, always scheduled (it follows money that is
/// already in flight, whatever the flag). One run: releases the payments whose time has come (and tries again those that failed), settles the
/// money of the requests that closed, drops the blocks that outlived their time, and warns about money held for too long.
/// </summary>
public interface ISupplierFundsJobService
{
    /// <summary>Runs the five steps. Each payment is handled on its own, under its lock; an error on one is logged and counted, and the others go on.</summary>
    Task<ServiceFundsRun> RunFundsAsync(CancellationToken cancellationToken = default);
}
