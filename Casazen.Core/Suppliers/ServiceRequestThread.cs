using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

public sealed record ServiceRequestMessageView(Guid Id, ServiceRequestMessageAuthor Author, string Body, DateTime CreatedAt, DateTime? ReadAt);

/// <summary>The thread of a request on measure: its messages in the order they were written, and whether a party can still add one.</summary>
/// <param name="CanWrite">The request is open (new, taken or in progress) and the party that reads it is allowed to write.</param>
/// <param name="RespondBy">The instant the supplier has to answer by, while nobody answered; <c>null</c> once it did.</param>
/// <param name="Offer">SP-10b-b, the supplier's reading only: the offer that waits for the customer's answer; <c>null</c> when there is none (and always for the customer's reading, whose offer is in its own area).</param>
/// <param name="Negotiation">SP-10b-b, the supplier's reading only: where the negotiation is (<see cref="ServiceRequestNegotiation"/>).</param>
/// <param name="CanSendOffer">SP-10b-b, the supplier's reading only: the request is new, the supplier is active and it may send an offer (a new one replaces the one that waits).</param>
public sealed record ServiceRequestThread(
    Guid RequestId,
    ServiceRequestStatus Status,
    bool CanWrite,
    DateTime? RespondBy,
    IReadOnlyList<ServiceRequestMessageView> Messages,
    ServiceRequestOffer? Offer = null,
    string? Negotiation = null,
    bool CanSendOffer = false);

public static class ServiceRequestThreadRules
{
    public static readonly TimeSpan NotificationQuietPeriod = TimeSpan.FromMinutes(10);

    public static bool IsOpen(ServiceRequestStatus status) =>
        status is ServiceRequestStatus.Richiesto or ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso;

    public static bool ShouldNotify(DateTime? previousByAuthorUtc, DateTime nowUtc) =>
        previousByAuthorUtc is not { } previous || nowUtc - previous >= NotificationQuietPeriod;
}

/// <summary>
/// Where the negotiation of a request on measure is (SP-10b, SP-10b-b), told to the customer and to the supplier. <b>Derived, never stored</b>:
/// from the status of the request, from whether the supplier wrote in the thread, from the offer that waits and from what became of the last one.
/// An open set: the frontend shows the states it knows and treats any other as <see cref="InConversation"/>.
/// </summary>
public static class ServiceRequestNegotiation
{
    public const string AwaitingSupplier = "AwaitingSupplier";

    public const string InConversation = "InConversation";

    public const string OfferSent = "OfferSent";

    public const string AwaitingPayment = "AwaitingPayment";

    public const string OfferDeclined = "OfferDeclined";

    public const string Agreed = "Agreed";

    public const string Declined = "Declined";

    public const string Expired = "Expired";

    /// <summary>Cancelled by the customer or closed because nobody answered in time.</summary>
    public const string Closed = "Closed";

    /// <param name="status">The status of the request.</param>
    /// <param name="supplierWrote">The supplier wrote at least once in the thread.</param>
    /// <param name="offerPending">An offer waits for the customer's answer.</param>
    /// <param name="lastOfferDeclined">The last thing that happened to an offer is that the customer turned it down.</param>
    /// <param name="cancellationReason">The reason of a cancelled request, to tell an offer that lapsed from the rest.</param>
    /// <param name="awaitingPayment">The customer accepted the offer and its card has not completed the payment yet.</param>
    public static string Of(
        ServiceRequestStatus status,
        bool supplierWrote,
        bool offerPending = false,
        bool lastOfferDeclined = false,
        string? cancellationReason = null,
        bool awaitingPayment = false) => status switch
        {
            ServiceRequestStatus.Richiesto when offerPending && awaitingPayment => AwaitingPayment,
            ServiceRequestStatus.Richiesto when offerPending => OfferSent,
            ServiceRequestStatus.Richiesto when lastOfferDeclined => OfferDeclined,
            ServiceRequestStatus.Richiesto => supplierWrote ? InConversation : AwaitingSupplier,
            ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso or ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato => Agreed,
            ServiceRequestStatus.Rifiutato => Declined,
            ServiceRequestStatus.Annullato when cancellationReason == ServiceRequestCancellationReasons.QuoteNotAnswered => Expired,
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
    public const string Closed = "service_request_thread_closed";

    public const string Full = "service_request_thread_full";

    public const string MessageInvalid = "service_request_message_invalid";

    public const string OfferNeeded = "supplier_inquiry_needs_offer";

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
