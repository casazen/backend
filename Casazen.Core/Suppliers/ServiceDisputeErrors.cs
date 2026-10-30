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
    /// <summary>404: the request has no dispute for the caller, or the dispute does not exist.</summary>
    public const string NotFound = "service_dispute_not_found";

    /// <summary>The key of <see cref="NotFound"/>.</summary>
    public const string NotFoundMessageKey = "ServiceDisputeNotFound";

    /// <summary>422: there is nothing to complain about from here: no money held by CasaZen for the job, or the request is not one that was done (or due to be).</summary>
    public const string NotReportable = "service_dispute_not_reportable";

    /// <summary>The key of <see cref="NotReportable"/>.</summary>
    public const string NotReportableMessageKey = "ServiceDisputeNotReportable";

    /// <summary>422: the work is not completed and the time agreed for it has not ended: a problem is reported once it has.</summary>
    public const string TooEarly = "service_dispute_too_early";

    /// <summary>The key of <see cref="TooEarly"/>.</summary>
    public const string TooEarlyMessageKey = "ServiceDisputeTooEarly";

    /// <summary>422: the time to report a problem is over (the money was released, is due, or the payer said "all fine"). The answer carries <c>windowEndedAt</c> when known.</summary>
    public const string WindowClosed = "service_dispute_window_closed";

    /// <summary>The key of <see cref="WindowClosed"/>.</summary>
    public const string WindowClosedMessageKey = "ServiceDisputeWindowClosed";

    /// <summary>422: a field of the complaint, of the reply or of the decision is not valid. The answer carries <c>fields</c>.</summary>
    public const string Invalid = "service_dispute_invalid";

    /// <summary>The key of <see cref="Invalid"/>.</summary>
    public const string InvalidMessageKey = "ServiceDisputeInvalid";

    /// <summary>409: the dispute is not open any more (it was decided or withdrawn): there is nothing to reply to, withdraw or decide.</summary>
    public const string NotOpen = "service_dispute_not_open";

    /// <summary>The key of <see cref="NotOpen"/>.</summary>
    public const string NotOpenMessageKey = "ServiceDisputeNotOpen";

    /// <summary>409: the supplier already replied; there is one reply.</summary>
    public const string AlreadyReplied = "service_dispute_already_replied";

    /// <summary>The key of <see cref="AlreadyReplied"/>.</summary>
    public const string AlreadyRepliedMessageKey = "ServiceDisputeAlreadyReplied";

    /// <summary>409: the supplier still has time to reply, so an admin cannot decide yet. The answer carries <c>supplierReplyDueAt</c>.</summary>
    public const string AwaitingSupplier = "service_dispute_awaiting_supplier";

    /// <summary>The key of <see cref="AwaitingSupplier"/>.</summary>
    public const string AwaitingSupplierMessageKey = "ServiceDisputeAwaitingSupplier";

    /// <summary>409: the dispute was decided in another way meanwhile (another admin, the payer withdrawing): read it again.</summary>
    public const string StateChanged = "service_dispute_state_changed";

    /// <summary>The key of <see cref="StateChanged"/>.</summary>
    public const string StateChangedMessageKey = "ServiceDisputeStateChanged";

    /// <summary>409: the payment is not in the state a decision needs (it is under review, or its money moved otherwise): an admin looks at the payment first.</summary>
    public const string PaymentChanged = "service_dispute_payment_changed";

    /// <summary>The key of <see cref="PaymentChanged"/>.</summary>
    public const string PaymentChangedMessageKey = "ServiceDisputePaymentChanged";

    /// <summary>422: the amount to give back is not a positive whole number of cents.</summary>
    public const string RefundInvalid = "service_dispute_refund_invalid";

    /// <summary>The key of <see cref="RefundInvalid"/>.</summary>
    public const string RefundInvalidMessageKey = "ServiceDisputeRefundInvalid";

    /// <summary>422: the amount to give back is more than the supplier's part that is left (the price minus the commission minus what was refunded). The answer carries <c>maxRefundCents</c>.</summary>
    public const string RefundExceeds = "service_dispute_refund_exceeds";

    /// <summary>The key of <see cref="RefundExceeds"/>.</summary>
    public const string RefundExceedsMessageKey = "ServiceDisputeRefundExceeds";

    /// <summary>422: the outcome of a decision is not one of <c>for_supplier</c> and <c>for_customer</c>.</summary>
    public const string OutcomeInvalid = "service_dispute_outcome_invalid";

    /// <summary>The key of <see cref="OutcomeInvalid"/>.</summary>
    public const string OutcomeInvalidMessageKey = "ServiceDisputeOutcomeInvalid";

    /// <summary>The key of the 400 for a body of the customer's complaint without a reason or with a text far too long (the form is malformed, not a rule broken).</summary>
    public const string FormInvalidMessageKey = "ServiceDisputeFormInvalid";

    /// <summary>Every <c>SharedResources</c> key of this feature (a test checks that each one exists in Italian and English, with a different text).</summary>
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
    ];

    /// <summary>The dispute (or the request's dispute) does not exist for the caller.</summary>
    public static NotFoundException DisputeNotFound() =>
        new("The service request has no dispute for the caller")
        {
            Code = NotFound,
            MessageKey = NotFoundMessageKey,
        };

    public static DomainRuleException CannotReport() => new(NotReportable, NotReportableMessageKey);

    public static DomainRuleException ReportTooEarly() => new(TooEarly, TooEarlyMessageKey);

    /// <summary>The window to report a problem is over; <paramref name="endedAt"/> is when it ended, if the payment says.</summary>
    public static DomainRuleException ReportWindowClosed(DateTime? endedAt) =>
        new(WindowClosed, WindowClosedMessageKey)
        {
            Extensions = endedAt is { } at
                ? new Dictionary<string, object?> { ["windowEndedAt"] = DateTime.SpecifyKind(at, DateTimeKind.Utc) }
                : new Dictionary<string, object?>(),
        };

    /// <summary>A 422 that names the fields at fault, as the booking does (<c>fields</c> next to <c>code</c>).</summary>
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

    public static DomainRuleException OutcomeNotValid() => new(OutcomeInvalid, OutcomeInvalidMessageKey);
}
