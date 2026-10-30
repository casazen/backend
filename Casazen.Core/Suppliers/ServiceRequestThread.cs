using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>One message of the thread of a request on measure, as a party reads it (SP-10b). The body is plain text.</summary>
/// <param name="Author">Who wrote it.</param>
/// <param name="ReadAt">When the <b>other</b> party first read it; <c>null</c> while it did not.</param>
public sealed record ServiceRequestMessageView(Guid Id, ServiceRequestMessageAuthor Author, string Body, DateTime CreatedAt, DateTime? ReadAt);

/// <summary>The thread of a request on measure: its messages in the order they were written, and whether a party can still add one.</summary>
/// <param name="CanWrite">The request is open (new, taken or in progress) and the party that reads it is allowed to write.</param>
/// <param name="RespondBy">The instant the supplier has to answer by, while nobody answered; <c>null</c> once it did.</param>
public sealed record ServiceRequestThread(
    Guid RequestId,
    ServiceRequestStatus Status,
    bool CanWrite,
    DateTime? RespondBy,
    IReadOnlyList<ServiceRequestMessageView> Messages);

/// <summary>
/// The rules of the conversation of a request on measure (SP-10b): when it is open, and when the other party is told of a new message.
/// Pure functions, no clock and no database.
/// </summary>
public static class ServiceRequestThreadRules
{
    /// <summary>
    /// After a message, the other party is not told of the next ones by the same author for this long: a person who writes three lines in a
    /// row gets one e-mail, not three. The thread is always there to be read.
    /// </summary>
    public static readonly TimeSpan NotificationQuietPeriod = TimeSpan.FromMinutes(10);

    /// <summary>A conversation goes on while the request is new, taken or in progress; once refused, cancelled or done it is read only.</summary>
    public static bool IsOpen(ServiceRequestStatus status) =>
        status is ServiceRequestStatus.Richiesto or ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso;

    /// <summary>
    /// True when the other party is told of a message written at <paramref name="nowUtc"/>: the author had not written in the last
    /// <see cref="NotificationQuietPeriod"/> (<paramref name="previousByAuthorUtc"/> is the time of its latest earlier message).
    /// </summary>
    public static bool ShouldNotify(DateTime? previousByAuthorUtc, DateTime nowUtc) =>
        previousByAuthorUtc is not { } previous || nowUtc - previous >= NotificationQuietPeriod;
}

/// <summary>
/// Where the negotiation of a request on measure is (SP-10b), told to the customer. <b>Derived, never stored</b>: from the status of the request
/// and from whether the supplier wrote in the thread. The offer (SP-10b-b) adds its own states to the ones of the conversation.
/// </summary>
public static class ServiceRequestNegotiation
{
    /// <summary>New, and the supplier has not written yet.</summary>
    public const string AwaitingSupplier = "AwaitingSupplier";

    /// <summary>New, and the supplier wrote: the two are talking.</summary>
    public const string InConversation = "InConversation";

    /// <summary>The work is agreed (the request was taken, started or done).</summary>
    public const string Agreed = "Agreed";

    /// <summary>The supplier refused the request.</summary>
    public const string Declined = "Declined";

    /// <summary>Cancelled by the customer or closed because nobody answered in time.</summary>
    public const string Closed = "Closed";

    public static string Of(ServiceRequestStatus status, bool supplierWrote) => status switch
    {
        ServiceRequestStatus.Richiesto => supplierWrote ? InConversation : AwaitingSupplier,
        ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso or ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato => Agreed,
        ServiceRequestStatus.Rifiutato => Declined,
        _ => Closed,
    };
}

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the conversation of a
/// request on measure (SP-10b). The codes are snake_case and never renamed: the frontend branches on them. A request that is not the
/// caller's, is not a request on measure or does not exist is the plain 404 of the service requests.
/// </summary>
public static class ServiceRequestThreadErrors
{
    /// <summary>409: the request is refused, cancelled or done: the conversation is closed.</summary>
    public const string Closed = "service_request_thread_closed";

    /// <summary>422: the thread already has the most messages it may have (<see cref="ShowcaseInquiryLimits.MaxMessagesPerRequest"/>).</summary>
    public const string Full = "service_request_thread_full";

    /// <summary>422: the message is empty, too long or has characters a message cannot have.</summary>
    public const string MessageInvalid = "service_request_message_invalid";

    /// <summary>
    /// 422: a request on measure is taken by accepting an offer (SP-10b-b), not by taking it or proposing a time: the work, its price and
    /// its time are agreed inside CasaZen.
    /// </summary>
    public const string OfferNeeded = "supplier_inquiry_needs_offer";

    /// <summary>Every <c>SharedResources</c> key the conversation raises (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        "ServiceRequestThreadClosed",
        "ServiceRequestThreadFull",
        "ServiceRequestMessageInvalid",
        "ServiceRequestInquiryNeedsOffer",
    ];

    public static DomainConflictException ThreadClosed() => new(Closed, "ServiceRequestThreadClosed");

    public static DomainRuleException ThreadFull() => new(Full, "ServiceRequestThreadFull");

    public static DomainRuleException InvalidMessage() => new(MessageInvalid, "ServiceRequestMessageInvalid");

    public static DomainRuleException NeedsAnOffer() => new(OfferNeeded, "ServiceRequestInquiryNeedsOffer");
}
