namespace Casazen.Core.Suppliers;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the errors
/// of the payment of a service request (SP-15a). A code is snake_case and never renamed: the frontend branches on it. The
/// public endpoints answer a wrong id and a wrong token with the same 404 (<see cref="LinkInvalid"/>), so a payment id alone
/// never says whether the payment exists.
/// </summary>
public static class ServicePaymentErrors
{
    public const string LinkInvalid = "service_payment_link_invalid";

    public const string LinkInvalidMessageKey = "ServicePaymentLinkInvalid";

    public const string NotFound = "service_payment_not_found";

    public const string NotFoundMessageKey = "ServicePaymentNotFound";

    public const string NotPayable = "service_payment_not_payable";

    public const string NotPayableMessageKey = "ServicePaymentNotPayable";

    public const string AlreadyPaidMessageKey = "ServicePaymentAlreadyPaid";

    /// <summary>409: a payment is being processed by Stripe (or needs to be reviewed): it must not be paid again.</summary>
    public const string InFlight = "service_payment_in_flight";

    public const string InFlightMessageKey = "ServicePaymentInFlight";

    public const string SupplierNotReady = "service_payment_supplier_not_ready";

    public const string SupplierNotReadyMessageKey = "ServicePaymentSupplierNotReady";

    public const string NotOnline = "service_payment_not_online";

    public const string NotOnlineMessageKey = "ServicePaymentNotOnline";

    public const string NotRequestable = "service_payment_not_requestable";

    public const string NotRequestableMessageKey = "ServicePaymentNotRequestable";

    public const string AmountUnconfirmed = "service_payment_amount_unconfirmed";

    public const string AmountUnconfirmedMessageKey = "ServicePaymentAmountUnconfirmed";

    public const string AmountRequired = "service_payment_amount_required";

    public const string AmountRequiredMessageKey = "ServicePaymentAmountRequired";

    public const string RequestTooSoon = "service_payment_request_too_soon";

    public const string RequestTooSoonMessageKey = "ServicePaymentRequestTooSoon";

    public const string NoRecipient = "service_payment_no_recipient";

    public const string NoRecipientMessageKey = "ServicePaymentNoRecipient";

    public const string RequestNotSent = "service_payment_request_not_sent";

    public const string RequestNotSentMessageKey = "ServicePaymentRequestNotSent";

    public const string OfflineReasonRequired = "service_payment_offline_reason_required";

    public const string OfflineReasonRequiredMessageKey = "ServicePaymentOfflineReasonRequired";

    /// <summary>409: the payment of the request changed while the call was running (a concurrent change won): nothing was saved.</summary>
    public const string StateChanged = "service_payment_state_changed";

    public const string StateChangedMessageKey = "ServicePaymentStateChanged";

    public const string NoConfirmationNeeded = "service_request_no_confirmation_needed";

    public const string NoConfirmationNeededMessageKey = "ServiceRequestNoConfirmationNeeded";

    public const string OnlinePayment = "service_request_online_payment";

    public const string OnlinePaymentMessageKey = "ServiceRequestOnlinePayment";

    public const string RefundNotRefundable = "service_payment_not_refundable";

    public const string RefundNotRefundableMessageKey = "ServicePaymentNotRefundable";

    public const string RefundOffline = "service_payment_refund_offline";

    public const string RefundOfflineMessageKey = "ServicePaymentRefundOffline";

    public const string RefundNothing = "service_payment_nothing_to_refund";

    public const string RefundNothingMessageKey = "ServicePaymentNothingToRefund";

    public const string RefundAmountInvalid = "service_payment_refund_amount_invalid";

    public const string RefundAmountInvalidMessageKey = "ServicePaymentRefundAmountInvalid";

    public const string RefundAmountExceeds = "service_payment_refund_amount_exceeds";

    public const string RefundAmountExceedsMessageKey = "ServicePaymentRefundAmountExceeds";

    public const string RefundReasonTooLongMessageKey = "ServicePaymentRefundReasonTooLong";

    public const string CommissionInvalid = "supplier_commission_invalid";

    public const string CommissionInvalidMessageKey = "SupplierCommissionInvalid";

    public const string CommissionUntilInvalid = "supplier_commission_until_invalid";

    public const string CommissionUntilInvalidMessageKey = "SupplierCommissionUntilInvalid";

    public const string CommissionReasonMessageKey = "SupplierCommissionReasonInvalid";

    public const string ExportMonthInvalid = "service_payment_export_month_invalid";

    public const string ExportMonthInvalidMessageKey = "ServicePaymentExportMonthInvalid";

    public const string CaptureFailed = "service_payment_capture_failed";

    public const string CaptureFailedMessageKey = "ServicePaymentCaptureFailed";

    public const string AuthorizationExpired = "service_payment_authorization_expired";

    public const string AuthorizationExpiredMessageKey = "ServicePaymentAuthorizationExpired";

    public const string AmountLocked = "service_payment_amount_locked";

    public const string AmountLockedMessageKey = "ServicePaymentAmountLocked";

    public const string NotConfirmable = "service_payment_not_confirmable";

    public const string NotConfirmableMessageKey = "ServicePaymentNotConfirmable";

    public const string HeldByCasaZen = "service_payment_held";

    public const string HeldByCasaZenMessageKey = "ServicePaymentHeld";

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
        CaptureFailedMessageKey,
        AuthorizationExpiredMessageKey,
        AmountLockedMessageKey,
        NotConfirmableMessageKey,
        HeldByCasaZenMessageKey,
    ];
}
