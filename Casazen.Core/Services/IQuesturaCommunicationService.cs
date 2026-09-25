namespace Casazen.Core.Services;

/// <summary>
/// Communication to the public-security authority for a lease with an extra-EU tenant (art. 7 D.Lgs. 286/1998, LT-07,
/// A7-08): CasaZen does not send it. The landlord declares the delivery date of the property (the 48 hours count from
/// it) and, once sent, the date of the communication with an optional receipt. The caller authorizes the lease first
/// (TN-3); another org's lease is not found (tenant filter).
/// </summary>
public interface IQuesturaCommunicationService
{
    /// <summary>
    /// Declares the delivery date of the property (a calendar date, not after the end of the lease), or clears it with
    /// <c>null</c> (the start date applies again). 422 <see cref="QuesturaCommunicationErrorCodes.DeliveryDateAfterEnd"/>.
    /// </summary>
    Task DeclareDeliveryDateAsync(
        Guid leaseId, string userId, DateTime? deliveryDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the landlord's declaration that the communication was sent on <see cref="QuesturaCommunicationDeclaration.CommunicationDate"/>,
    /// with the optional receipt (PDF, private bucket). Only now the checklist item is ticked
    /// (event <c>QuesturaCommunicationMarkedDone</c>). 422 when the lease has no extra-EU tenant, the date is after today
    /// or the receipt is not a PDF; 409 when it was already declared.
    /// </summary>
    Task MarkDoneAsync(
        Guid leaseId, string userId, QuesturaCommunicationDeclaration declaration, CancellationToken cancellationToken = default);

    /// <summary>The stored receipt; 404 <see cref="QuesturaCommunicationErrorCodes.ReceiptNotAvailable"/> when there is none.</summary>
    Task<QuesturaReceiptFile> OpenReceiptAsync(Guid leaseId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The landlord's declaration: the date the communication was sent (calendar date, not after today) and, optionally, the
/// receipt (PDF checked on its content, at most <see cref="QuesturaCommunicationLimits.MaxReceiptBytes"/>).
/// </summary>
public sealed record QuesturaCommunicationDeclaration(DateTime CommunicationDate, Stream? Receipt = null, long? ReceiptLength = null);

/// <summary>The stored receipt; the caller disposes <see cref="Content"/>.</summary>
public sealed record QuesturaReceiptFile(Stream Content, string FileName);

/// <summary>Input limits of the Questura declaration.</summary>
public static class QuesturaCommunicationLimits
{
    /// <summary>Largest receipt accepted, the same as the RLI receipt.</summary>
    public const long MaxReceiptBytes = RliRegistrationLimits.MaxReceiptBytes;
}

/// <summary>Error codes of the Questura communication (ProblemDetails <c>code</c>, FD-05).</summary>
public static class QuesturaCommunicationErrorCodes
{
    public const string NotRequired = "questura_not_required";
    public const string AlreadyMarkedDone = "questura_already_marked_done";
    public const string DateInFuture = "questura_communication_date_in_future";
    public const string ReceiptInvalid = "questura_receipt_invalid";
    public const string ReceiptNotAvailable = "questura_receipt_not_available";
    public const string DeliveryDateAfterEnd = "questura_delivery_date_after_end";
}
