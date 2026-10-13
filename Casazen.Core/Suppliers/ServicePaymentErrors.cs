namespace Casazen.Core.Suppliers;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the errors
/// of the payment of a service request (SP-15a). A code is snake_case and never renamed: the frontend branches on it. The
/// public endpoints answer a wrong id and a wrong token with the same 404 (<see cref="LinkInvalid"/>), so a payment id alone
/// never says whether the payment exists.
/// </summary>
public static class ServicePaymentErrors
{
    /// <summary>404: the payment link does not match a payment (wrong id, wrong token, or a link that is no longer valid).</summary>
    public const string LinkInvalid = "service_payment_link_invalid";

    /// <summary>The key of <see cref="LinkInvalid"/>.</summary>
    public const string LinkInvalidMessageKey = "ServicePaymentLinkInvalid";

    /// <summary>404: the request has no payment for the caller (the host session of a request that is not paid inside CasaZen, or not completed yet).</summary>
    public const string NotFound = "service_payment_not_found";

    /// <summary>The key of <see cref="NotFound"/>.</summary>
    public const string NotFoundMessageKey = "ServicePaymentNotFound";

    /// <summary>409: the payment can no longer be paid (it is paid, withdrawn or refunded).</summary>
    public const string NotPayable = "service_payment_not_payable";

    /// <summary>The key of <see cref="NotPayable"/>.</summary>
    public const string NotPayableMessageKey = "ServicePaymentNotPayable";

    /// <summary>The key of the 409 <see cref="NotPayable"/> for a payment that is already paid.</summary>
    public const string AlreadyPaidMessageKey = "ServicePaymentAlreadyPaid";

    /// <summary>409: a payment is being processed by Stripe (or needs to be reviewed): it must not be paid again.</summary>
    public const string InFlight = "service_payment_in_flight";

    /// <summary>The key of <see cref="InFlight"/>.</summary>
    public const string InFlightMessageKey = "ServicePaymentInFlight";

    /// <summary>409: the supplier's Stripe account cannot take charges and payouts now, so the payment cannot be started.</summary>
    public const string SupplierNotReady = "service_payment_supplier_not_ready";

    /// <summary>The key of <see cref="SupplierNotReady"/>.</summary>
    public const string SupplierNotReadyMessageKey = "ServicePaymentSupplierNotReady";

    /// <summary>422: the request is not paid inside CasaZen (<c>PaymentMode = Manual</c>): there is no payment to ask for.</summary>
    public const string NotOnline = "service_payment_not_online";

    /// <summary>The key of <see cref="NotOnline"/>.</summary>
    public const string NotOnlineMessageKey = "ServicePaymentNotOnline";

    /// <summary>422: the payment can be asked only for a completed request that is not paid yet.</summary>
    public const string NotRequestable = "service_payment_not_requestable";

    /// <summary>The key of <see cref="NotRequestable"/>.</summary>
    public const string NotRequestableMessageKey = "ServicePaymentNotRequestable";

    /// <summary>422: the final amount is above the quote by more than the tolerance and the host has not confirmed it yet (decision D7).</summary>
    public const string AmountUnconfirmed = "service_payment_amount_unconfirmed";

    /// <summary>The key of <see cref="AmountUnconfirmed"/>.</summary>
    public const string AmountUnconfirmedMessageKey = "ServicePaymentAmountUnconfirmed";

    /// <summary>422: the request has no final amount to charge or to record.</summary>
    public const string AmountRequired = "service_payment_amount_required";

    /// <summary>The key of <see cref="AmountRequired"/>.</summary>
    public const string AmountRequiredMessageKey = "ServicePaymentAmountRequired";

    /// <summary>422: the supplier asked again before a day went by.</summary>
    public const string RequestTooSoon = "service_payment_request_too_soon";

    /// <summary>The key of <see cref="RequestTooSoon"/>.</summary>
    public const string RequestTooSoonMessageKey = "ServicePaymentRequestTooSoon";

    /// <summary>422: the payer org has no email address to send the payment link to.</summary>
    public const string NoRecipient = "service_payment_no_recipient";

    /// <summary>The key of <see cref="NoRecipient"/>.</summary>
    public const string NoRecipientMessageKey = "ServicePaymentNoRecipient";

    /// <summary>422: the email could not be queued (e.g. the email provider is not configured): nothing was sent and nothing changed.</summary>
    public const string RequestNotSent = "service_payment_request_not_sent";

    /// <summary>The key of <see cref="RequestNotSent"/>.</summary>
    public const string RequestNotSentMessageKey = "ServicePaymentRequestNotSent";

    /// <summary>422: a request paid inside CasaZen can be recorded as paid outside only with a reason (decision D5).</summary>
    public const string OfflineReasonRequired = "service_payment_offline_reason_required";

    /// <summary>The key of <see cref="OfflineReasonRequired"/>.</summary>
    public const string OfflineReasonRequiredMessageKey = "ServicePaymentOfflineReasonRequired";

    /// <summary>409: the payment of the request changed while the call was running (a concurrent change won): nothing was saved.</summary>
    public const string StateChanged = "service_payment_state_changed";

    /// <summary>The key of <see cref="StateChanged"/>.</summary>
    public const string StateChangedMessageKey = "ServicePaymentStateChanged";

    /// <summary>422: the host has nothing to confirm (the final amount is not above the quote, or the request is not completed).</summary>
    public const string NoConfirmationNeeded = "service_request_no_confirmation_needed";

    /// <summary>The key of <see cref="NoConfirmationNeeded"/>.</summary>
    public const string NoConfirmationNeededMessageKey = "ServiceRequestNoConfirmationNeeded";

    /// <summary>
    /// 422: the request is paid inside CasaZen, so the host cannot mark it paid by hand (decision D5): the payer pays online, or
    /// the supplier records the exception.
    /// </summary>
    public const string OnlinePayment = "service_request_online_payment";

    /// <summary>The key of <see cref="OnlinePayment"/>.</summary>
    public const string OnlinePaymentMessageKey = "ServiceRequestOnlinePayment";

    // ─── Refunds and the admin tools (SP-15b) ────────────────────────────────────────────────────────────────────────

    /// <summary>422: the payment cannot be refunded: it is not paid online (still to be paid, in flight, withdrawn, or waiting for a review).</summary>
    public const string RefundNotRefundable = "service_payment_not_refundable";

    /// <summary>The key of <see cref="RefundNotRefundable"/>.</summary>
    public const string RefundNotRefundableMessageKey = "ServicePaymentNotRefundable";

    /// <summary>422: it was recorded as received outside CasaZen: no money went through CasaZen, so CasaZen has nothing to refund.</summary>
    public const string RefundOffline = "service_payment_refund_offline";

    /// <summary>The key of <see cref="RefundOffline"/>.</summary>
    public const string RefundOfflineMessageKey = "ServicePaymentRefundOffline";

    /// <summary>422: everything that could be refunded has been refunded, or is being refunded.</summary>
    public const string RefundNothing = "service_payment_nothing_to_refund";

    /// <summary>The key of <see cref="RefundNothing"/>.</summary>
    public const string RefundNothingMessageKey = "ServicePaymentNothingToRefund";

    /// <summary>422: the amount to refund is not a positive number of cents.</summary>
    public const string RefundAmountInvalid = "service_payment_refund_amount_invalid";

    /// <summary>The key of <see cref="RefundAmountInvalid"/>.</summary>
    public const string RefundAmountInvalidMessageKey = "ServicePaymentRefundAmountInvalid";

    /// <summary>422: the amount is more than what can still be refunded (argument 0: the most, in euro).</summary>
    public const string RefundAmountExceeds = "service_payment_refund_amount_exceeds";

    /// <summary>The key of <see cref="RefundAmountExceeds"/>.</summary>
    public const string RefundAmountExceedsMessageKey = "ServicePaymentRefundAmountExceeds";

    /// <summary>The key of the 400 for a refund note longer than <see cref="ServicePaymentLimits.OfflineNoteMaxLength"/> characters.</summary>
    public const string RefundReasonTooLongMessageKey = "ServicePaymentRefundReasonTooLong";

    /// <summary>422: the commission of a supplier must be a percentage from 0 to the highest the configuration allows, with two decimals at most (argument 0: the highest).</summary>
    public const string CommissionInvalid = "supplier_commission_invalid";

    /// <summary>The key of <see cref="CommissionInvalid"/>.</summary>
    public const string CommissionInvalidMessageKey = "SupplierCommissionInvalid";

    /// <summary>422: the end of a commission period must come with a percentage and be in the future.</summary>
    public const string CommissionUntilInvalid = "supplier_commission_until_invalid";

    /// <summary>The key of <see cref="CommissionUntilInvalid"/>.</summary>
    public const string CommissionUntilInvalidMessageKey = "SupplierCommissionUntilInvalid";

    /// <summary>The key of the 400 for a missing or too long reason of a commission change.</summary>
    public const string CommissionReasonMessageKey = "SupplierCommissionReasonInvalid";

    /// <summary>422: the month of the commission export is not a past or current month in the form <c>yyyy-MM</c>.</summary>
    public const string ExportMonthInvalid = "service_payment_export_month_invalid";

    /// <summary>The key of <see cref="ExportMonthInvalid"/>.</summary>
    public const string ExportMonthInvalidMessageKey = "ServicePaymentExportMonthInvalid";

    /// <summary>Every <c>SharedResources</c> key of this feature (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        LinkInvalidMessageKey,
        NotFoundMessageKey,
        NotPayableMessageKey,
        AlreadyPaidMessageKey,
        InFlightMessageKey,
        SupplierNotReadyMessageKey,
        NotOnlineMessageKey,
        NotRequestableMessageKey,
        AmountUnconfirmedMessageKey,
        AmountRequiredMessageKey,
        RequestTooSoonMessageKey,
        NoRecipientMessageKey,
        RequestNotSentMessageKey,
        OfflineReasonRequiredMessageKey,
        StateChangedMessageKey,
        NoConfirmationNeededMessageKey,
        OnlinePaymentMessageKey,
        RefundNotRefundableMessageKey,
        RefundOfflineMessageKey,
        RefundNothingMessageKey,
        RefundAmountInvalidMessageKey,
        RefundAmountExceedsMessageKey,
        RefundReasonTooLongMessageKey,
        CommissionInvalidMessageKey,
        CommissionUntilInvalidMessageKey,
        CommissionReasonMessageKey,
        ExportMonthInvalidMessageKey,
    ];
}
