using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Everything the rules of the two payment actions of the supplier look at (SP-04e): the request, its live payment (the one that is
/// not <see cref="ServicePaymentStatus.Canceled"/>, if any), and the few facts around them. A plain value, so the rules below are
/// pure and a test can run them against every combination.
/// </summary>
/// <param name="RequestStatus">Where the request is.</param>
/// <param name="PaymentMode">How the request is paid, fixed when the supplier took it.</param>
/// <param name="FinalAmountCents">The total the supplier declared when it completed the work.</param>
/// <param name="FinalAmountNeedsConfirmation">The amount is above the quote by more than the tolerance and the host did not confirm it.</param>
/// <param name="PaymentStatus">The status of the live payment; null when the request has none.</param>
/// <param name="PaymentLastSentAt">When the payer was last sent a link; null when never.</param>
/// <param name="OnlinePaymentsEnabled">The feature flag <c>SupplierOnlinePayments</c> is on.</param>
/// <param name="SupplierMayAct">
/// The supplier is <c>Active</c> and has accepted the current Terms of Service: a suspended supplier, or one that has to accept the new
/// Terms, performs no action at all (SU-12, SU-05).
/// </param>
/// <param name="SupplierCanReceivePayments">The supplier's Stripe account takes charges and payouts (<see cref="SupplierVerification.CanReceivePayments"/>).</param>
/// <param name="PayerHasEmail">The org that pays has a contact address to send the link to; a private customer of the showcase has none.</param>
/// <param name="MinAmountCents">The smallest amount charged inside CasaZen (<c>SupplierPayments:MinAmountCents</c>).</param>
public sealed record ServicePaymentActionFacts(
    ServiceRequestStatus RequestStatus,
    ServiceRequestPaymentMode PaymentMode,
    int? FinalAmountCents,
    bool FinalAmountNeedsConfirmation,
    ServicePaymentStatus? PaymentStatus,
    DateTime? PaymentLastSentAt,
    bool OnlinePaymentsEnabled,
    bool SupplierMayAct,
    bool SupplierCanReceivePayments,
    bool PayerHasEmail,
    int MinAmountCents);

/// <summary>
/// Whether the supplier may ask for the payment of a request, and whether it may record it as received outside CasaZen (SP-04e): the
/// <b>same rules</b>, in the same order, as <c>POST api/supplier/requests/{id}/payment-request</c> and <c>…/payment/offline</c>
/// (<c>SupplierPaymentService.RequestPaymentAsync</c> / <c>RecordOfflineAsync</c>, SP-15a), so a button of the console and the answer of the
/// server agree. A test runs the real services against every combination of the facts to keep it so
/// (<c>SupplierPaymentSummaryTests</c>); change the rules of an endpoint and this class together.
/// </summary>
/// <remarks>
/// <para>What cannot be known without asking Stripe is not here: a payment that is paid or being processed there while CasaZen still
/// says <c>Requested</c> makes the endpoint answer 409 although the rules allow it. That is a race, not a rule.</para>
/// <para>The reason that is mandatory when an <c>Online</c> request is recorded as paid outside CasaZen is an input of the action, not a
/// condition of it.</para>
/// </remarks>
public static class ServicePaymentActions
{
    /// <summary>
    /// The supplier may ask for the payment (the first link, or a reminder with a new one): the flag is on, the supplier may act, the
    /// request is paid inside CasaZen and completed with an amount nobody still has to confirm and of at least the minimum, the supplier
    /// can be paid, the payer has an address, the payment is not settled or in flight, and no link went out in the last day.
    /// </summary>
    public static bool CanRequest(ServicePaymentActionFacts facts, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // The endpoint is behind the flag (404 before authentication while it is off).
        if (!facts.OnlinePaymentsEnabled)
            return false;

        // EnsureRequestable: paid inside CasaZen, completed, nothing left to confirm.
        if (!facts.SupplierMayAct
            || facts.PaymentMode != ServiceRequestPaymentMode.Online
            || facts.RequestStatus != ServiceRequestStatus.Completato
            || facts.FinalAmountNeedsConfirmation)
            return false;

        if (facts.FinalAmountCents is not { } amount || amount < facts.MinAmountCents)
            return false;

        if (!facts.SupplierCanReceivePayments || !facts.PayerHasEmail)
            return false;

        if (facts.PaymentStatus is { } status && IsSettledOrInFlight(status))
            return false;

        // At most one request or reminder a day.
        return facts.PaymentLastSentAt is not { } lastSent
            || now - lastSent >= TimeSpan.FromHours(ServicePaymentLimits.MinHoursBetweenRequests);
    }

    /// <summary>
    /// The supplier may record that it was paid outside CasaZen: it may act, the request can still become <c>Pagato</c>, it has a final
    /// amount, and its payment is not already paid or in flight. Not behind the flag.
    /// </summary>
    public static bool CanRecordOffline(ServicePaymentActionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.SupplierMayAct || !ServiceRequestStateMachine.CanTransition(facts.RequestStatus, ServiceRequestStatus.Pagato))
            return false;

        if (facts.FinalAmountCents is null)
            return false;

        return facts.PaymentStatus is not { } status || !IsSettledOrInFlight(status);
    }

    /// <summary>The payment was collected (and perhaps refunded), is being processed by Stripe, or waits for an admin: nothing can be asked or recorded.</summary>
    private static bool IsSettledOrInFlight(ServicePaymentStatus status) => status is
        ServicePaymentStatus.Paid
        or ServicePaymentStatus.PartiallyRefunded
        or ServicePaymentStatus.Refunded
        or ServicePaymentStatus.Processing
        or ServicePaymentStatus.NeedsReview;
}
