using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>Where a payment link stands for the payer who opens it (<see cref="ISupplierPaymentService.GetPublicAsync"/>). Serialized by name.</summary>
public enum PublicServicePaymentState
{
    /// <summary>Nothing was collected and the supplier can be paid: the payer can start the payment.</summary>
    Payable,

    /// <summary>Stripe is processing a payment (or it needs to be checked): the payer must not pay again.</summary>
    Processing,

    /// <summary>Paid (a refund does not make it payable again).</summary>
    Paid,

    /// <summary>Not payable now: withdrawn, or the supplier cannot be paid through CasaZen at the moment.</summary>
    Unavailable,
}

/// <summary>
/// The payment page of a service as the payer sees it. It names the supplier, the work, the property (the payer's own) and the
/// price, never the commission, a name, an address or a contact: the link is the only access check.
/// </summary>
/// <param name="LastAttemptFailed">The last online attempt failed; the payer can try again.</param>
/// <param name="ValidUntil">When the link stops working (counted from the email that carried it); null once it is paid or processing.</param>
public sealed record PublicServicePayment(
    Guid Id,
    string SupplierName,
    string ServiceName,
    string PropertyName,
    DateTime? CompletedAt,
    int AmountCents,
    string Currency,
    IReadOnlyList<ServiceRequestPriceLine> Lines,
    PublicServicePaymentState State,
    bool LastAttemptFailed,
    DateTime? ValidUntil);

/// <summary>
/// What Stripe.js needs to confirm the payment with the Payment Element on the supplier's connected account (the same shape as
/// the rent payment session).
/// </summary>
/// <param name="ClientSecret">A credential: sent once to the payer's browser, never logged and never stored.</param>
/// <param name="PublishableKey">The platform's publishable key.</param>
/// <param name="StripeAccountId">The supplier's connected account (<c>acct_…</c>), needed by Stripe.js to load the PaymentIntent.</param>
public sealed record ServicePaymentSession(
    Guid PaymentId,
    string ClientSecret,
    string PublishableKey,
    string StripeAccountId,
    int AmountCents,
    string Currency);

/// <summary>
/// What <see cref="ISupplierPaymentService.PlanAsync"/> decided at the moment a request is completed (or its amount confirmed):
/// the mode the request keeps, and the payment to save together with the request, if any.
/// </summary>
/// <param name="Mode">The mode of the request from now on (a request that cannot be charged online falls back to <see cref="ServiceRequestPaymentMode.Manual"/>).</param>
/// <param name="Payment">The new payment, added to the context but not saved: the caller saves it with the request. Null when none is created.</param>
/// <param name="Token">The raw token of the payment link to send; null when the link cannot be sent now (pending). Only its hash is stored.</param>
public sealed record ServicePaymentPlan(ServiceRequestPaymentMode Mode, ServiceRequestPayment? Payment, string? Token);

/// <summary>
/// The payment of the service requests inside CasaZen (SP-15a, decisions D2, D3 and D5): the mode a request is paid in, the
/// payment row with the commission snapshotted, the Stripe session of the payer (anonymous with the link, or the signed-in host),
/// the request and the reminder the supplier sends, and the exception when the supplier records a payment received outside.
/// Money is a <b>direct charge</b> on the supplier's own Stripe account: CasaZen holds no funds. Webhooks, reminders, refunds and
/// the admin tools are SP-15b.
/// </summary>
/// <remarks>
/// <para>Every method that creates, reuses or drops the PaymentIntent of a request, re-issues a link or records an offline
/// payment runs under the advisory lock <c>ServiceRequestPayment</c> (key: the request id) in a READ COMMITTED transaction, so
/// two sessions of the same payment create one PaymentIntent. The first link, issued with the completion, is protected by the
/// <c>xmin</c> check of the request and by the unique index "one payment per request that is not canceled". Errors are
/// <c>DomainRuleException</c> (422), <c>DomainConflictException</c> (409) and <c>NotFoundException</c> (404) with the codes of
/// <see cref="ServicePaymentErrors"/>.</para>
/// <para>The feature flag <c>SupplierOnlinePayments</c> stops the <b>creation</b> of payments and payment requests only: the
/// public page and the session of a payment that exists stay available (money in flight).</para>
/// </remarks>
public interface ISupplierPaymentService
{
    /// <summary>
    /// The mode a request is paid in when the supplier takes it: <see cref="ServiceRequestPaymentMode.Online"/> only if the flag is
    /// on and the supplier's Stripe account can take charges <b>and</b> payouts; <see cref="ServiceRequestPaymentMode.Manual"/>
    /// otherwise (no payment before the KYC).
    /// </summary>
    Task<ServiceRequestPaymentMode> ResolveModeAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What happens to the payment when a request is completed with <paramref name="finalAmountCents"/> (or when the host confirms
    /// that amount): a request that is <c>Online</c>, with the flag still on and an amount of at least
    /// <c>MinAmountCents</c>, gets a <see cref="ServiceRequestPayment"/> (added to the context, not saved) unless the amount still
    /// needs the host's confirmation (<paramref name="needsConfirmation"/>); anything else falls back to
    /// <see cref="ServiceRequestPaymentMode.Manual"/> with no payment. The commission is computed here, with the supplier's own
    /// percentage or the platform's, and kept on the row.
    /// </summary>
    Task<ServicePaymentPlan> PlanAsync(
        ServiceRequest request,
        int? finalAmountCents,
        IReadOnlyList<ServiceRequestPriceLine> lines,
        bool needsConfirmation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// After the request was saved: sends the payment link to the payer when the plan has one. A link that cannot be queued is
    /// taken back (the payment stays pending, the supplier asks again); never an error for the caller, the request is already saved.
    /// </summary>
    Task AnnounceAsync(ServicePaymentPlan plan, ServiceRequest request, CancellationToken cancellationToken = default);

    /// <summary>The caller could not save the request: forget the payment that <see cref="PlanAsync"/> added to the context.</summary>
    void Discard(ServicePaymentPlan plan);

    /// <summary>
    /// The payment page of a link (anonymous: the token is the only access check). A wrong id, a wrong token and a link past its
    /// validity answer the same 404 <see cref="ServicePaymentErrors.LinkInvalid"/>.
    /// </summary>
    Task<PublicServicePayment> GetPublicAsync(Guid paymentId, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// The PaymentIntent the payer confirms with the Payment Element, created or reused (never two payable at once) under the
    /// payment lock. 409 <see cref="ServicePaymentErrors.NotPayable"/> (paid or withdrawn),
    /// <see cref="ServicePaymentErrors.InFlight"/> (a payment is being processed) or
    /// <see cref="ServicePaymentErrors.SupplierNotReady"/> (the supplier cannot take charges and payouts now).
    /// </summary>
    Task<ServicePaymentSession> CreatePublicSessionAsync(Guid paymentId, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same session for the signed-in host that owns the request (<paramref name="hostOrgId"/>), without the link. 404
    /// <see cref="ServicePaymentErrors.NotFound"/> when the request has no payment of that host.
    /// </summary>
    Task<ServicePaymentSession> CreateHostSessionAsync(Guid requestId, Guid hostOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier sends the payment request of a completed request paid inside CasaZen, or reminds the payer (at most once a day).
    /// Creates the payment when it does not exist yet (the request was completed while the supplier could not be paid, or with the
    /// flag off). The caller has already checked that the supplier owns the request and is active.
    /// </summary>
    Task<ServiceRequestPayment> RequestPaymentAsync(ServiceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier records that it was paid outside CasaZen (decision D5): the request becomes <c>Pagato</c>, any payment still
    /// waiting is withdrawn (its PaymentIntent canceled), and an offline payment row without commission is kept. On a request paid
    /// inside CasaZen the <paramref name="reason"/> is required and is the trace of the exception; a payment that is paid or in
    /// flight on Stripe makes it a 409. The host is told.
    /// </summary>
    Task<ServiceRequestPayment> RecordOfflineAsync(
        ServiceRequest request,
        string userId,
        string? reason,
        CancellationToken cancellationToken = default);
}
