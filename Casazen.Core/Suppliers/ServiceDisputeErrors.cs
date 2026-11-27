using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the complaint about a job paid
/// through CasaZen and its dispute (SP-15c-c). A code is snake_case and never renamed: the frontend branches on it. The public endpoints answer a
/// booking the caller cannot prove with the uniform 404 of the booking area (<c>supplier_booking_not_found</c>), so nothing here says whether a booking
/// exists; <see cref="NotFound"/> is for the supplier's and the admins' own reads.
/// </summary>
public static class ServiceDisputeErrors
{
    public const string NotFound = "service_dispute_not_found";

    public const string NotFoundMessageKey = "ServiceDisputeNotFound";

    public const string NotReportable = "service_dispute_not_reportable";

    public const string NotReportableMessageKey = "ServiceDisputeNotReportable";

    public const string TooEarly = "service_dispute_too_early";

    public const string TooEarlyMessageKey = "ServiceDisputeTooEarly";

    public const string WindowClosed = "service_dispute_window_closed";

    public const string WindowClosedMessageKey = "ServiceDisputeWindowClosed";

    public const string Invalid = "service_dispute_invalid";

    public const string InvalidMessageKey = "ServiceDisputeInvalid";

    public const string NotOpen = "service_dispute_not_open";

    public const string NotOpenMessageKey = "ServiceDisputeNotOpen";

    public const string AlreadyReplied = "service_dispute_already_replied";

    public const string AlreadyRepliedMessageKey = "ServiceDisputeAlreadyReplied";

    public const string AwaitingSupplier = "service_dispute_awaiting_supplier";

    public const string AwaitingSupplierMessageKey = "ServiceDisputeAwaitingSupplier";

    public const string StateChanged = "service_dispute_state_changed";

    public const string StateChangedMessageKey = "ServiceDisputeStateChanged";

    /// <summary>409: the payment is not in the state a decision needs (it is under review, or its money moved otherwise): an admin looks at the payment first.</summary>
    public const string PaymentChanged = "service_dispute_payment_changed";

    public const string PaymentChangedMessageKey = "ServiceDisputePaymentChanged";

    public const string RefundInvalid = "service_dispute_refund_invalid";

    public const string RefundInvalidMessageKey = "ServiceDisputeRefundInvalid";

    public const string RefundExceeds = "service_dispute_refund_exceeds";

    public const string RefundExceedsMessageKey = "ServiceDisputeRefundExceeds";

    public const string OutcomeInvalid = "service_dispute_outcome_invalid";

    public const string OutcomeInvalidMessageKey = "ServiceDisputeOutcomeInvalid";

    public const string OpenBlocksCompletion = "service_dispute_open";

    public const string OpenBlocksCompletionMessageKey = "ServiceDisputeOpenBlocksCompletion";

    public const string FormInvalidMessageKey = "ServiceDisputeFormInvalid";

    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        NotFoundMessageKey,
        NotReportableMessageKey,
        TooEarlyMessageKey,
        WindowClosedMessageKey,
        InvalidMessageKey,
        NotOpenMessageKey,
        AlreadyRepliedMessageKey,
        AwaitingSupplierMessageKey,
        StateChangedMessageKey,
        PaymentChangedMessageKey,
        RefundInvalidMessageKey,
        RefundExceedsMessageKey,
        OutcomeInvalidMessageKey,
        FormInvalidMessageKey,
        OpenBlocksCompletionMessageKey,
    ];

    public static NotFoundException DisputeNotFound() =>
        new("The service request has no dispute for the caller")
        {
            Code = NotFound,
            MessageKey = NotFoundMessageKey,
        };

    public static DomainRuleException CannotReport() => new(NotReportable, NotReportableMessageKey);

    public static DomainRuleException ReportTooEarly() => new(TooEarly, TooEarlyMessageKey);

    public static DomainRuleException ReportWindowClosed(DateTime? endedAt) =>
        new(WindowClosed, WindowClosedMessageKey)
        {
            Extensions = endedAt is { } at
                ? new Dictionary<string, object?> { ["windowEndedAt"] = DateTime.SpecifyKind(at, DateTimeKind.Utc) }
                : new Dictionary<string, object?>(),
        };

    public static DomainRuleException InvalidFields(params string[] fields) =>
        new(Invalid, InvalidMessageKey, string.Join(", ", fields))
        {
            Extensions = new Dictionary<string, object?> { ["fields"] = fields },
        };

    public static DomainConflictException DisputeNotOpen() => new(NotOpen, NotOpenMessageKey);

    public static DomainConflictException ReplyAlreadyGiven() => new(AlreadyReplied, AlreadyRepliedMessageKey);

    public static DomainConflictException SupplierStillHasTime(DateTime replyDueAt) =>
        new(AwaitingSupplier, AwaitingSupplierMessageKey)
        {
            Extensions = new Dictionary<string, object?>
            {
                ["supplierReplyDueAt"] = DateTime.SpecifyKind(replyDueAt, DateTimeKind.Utc),
            },
        };

    public static DomainConflictException DisputeChanged() => new(StateChanged, StateChangedMessageKey);

    public static DomainConflictException PaymentNotDecidable() => new(PaymentChanged, PaymentChangedMessageKey);

    public static DomainRuleException RefundAmountInvalid() => new(RefundInvalid, RefundInvalidMessageKey);

    public static DomainRuleException RefundAmountExceeds(int maxCents) =>
        new(RefundExceeds, RefundExceedsMessageKey, maxCents / 100m)
        {
            Extensions = new Dictionary<string, object?> { ["maxRefundCents"] = maxCents },
        };

    public static DomainConflictException CompletionBlockedByDispute() => new(OpenBlocksCompletion, OpenBlocksCompletionMessageKey);

    public static DomainRuleException OutcomeNotValid() => new(OutcomeInvalid, OutcomeInvalidMessageKey);
}
