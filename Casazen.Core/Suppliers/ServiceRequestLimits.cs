namespace Casazen.Core.Suppliers;

/// <summary>
/// Technical bounds of the schedule, price and lifecycle fields of a service request (SP-04). They live in one place so the
/// entity, the database checks, the rules and the request DTOs cannot drift apart. Like the limits of the catalog, they keep
/// a request small and a typo harmless: they are not product rules.
/// </summary>
public static class ServiceRequestLimits
{
    /// <summary>Longest cancellation reason (same size as the rejection reason).</summary>
    public const int CancellationReasonMaxLength = 500;

    /// <summary>Longest note the supplier leaves when it completes a request (the host's own notes are a separate column).</summary>
    public const int CompletionNotesMaxLength = 1000;

    /// <summary>Longest message that goes with a proposed time.</summary>
    public const int ProposalMessageMaxLength = 500;

    /// <summary>Longest name of the service kept on the request (the size of a catalog service name).</summary>
    public const int ServiceNameMaxLength = SupplierServiceCatalogLimits.NameMaxLength;

    /// <summary>
    /// Highest amount, in cents (100,000.00 EUR): the bound of the catalog, so the price of a request can never be above what
    /// the catalog lets a supplier ask. The lowest amount is 1 cent: "no price" is <c>null</c>.
    /// </summary>
    public const int MaxAmountCents = SupplierServiceCatalogLimits.MaxAmountCents;

    /// <summary>Extra lines (a bathroom more, extra linen) a supplier may add when it completes a request.</summary>
    public const int MaxExtras = 10;

    /// <summary>Longest label of an extra line.</summary>
    public const int ExtraLabelMaxLength = 80;

    /// <summary>Photos of the work a request keeps.</summary>
    public const int MaxWorkPhotos = 6;

    /// <summary>Photos accepted by one upload request (the 413 limit of the endpoint follows from it).</summary>
    public const int MaxFilesPerRequest = MaxWorkPhotos;

    /// <summary>Largest accepted photo, in bytes (10 MB, like every photo: <c>IImageStorageService.ValidateImage</c>).</summary>
    public const long MaxPhotoFileSizeBytes = SupplierServiceCatalogLimits.MaxPhotoFileSizeBytes;

    /// <summary>Upper bound of one upload request: <see cref="MaxFilesPerRequest"/> photos plus the multipart overhead.</summary>
    public const long MaxUploadRequestBytes = MaxFilesPerRequest * MaxPhotoFileSizeBytes + 1024 * 1024;

    /// <summary>Requests the supplier may accept in one batch (<c>POST api/supplier/inbox/accept</c>).</summary>
    public const int MaxBatchAccept = 20;

    /// <summary>Longest service filter of the inbox (a category code, or a service id).</summary>
    public const int InboxFilterMaxLength = 100;
}
