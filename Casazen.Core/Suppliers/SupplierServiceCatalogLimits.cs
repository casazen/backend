namespace Casazen.Core.Suppliers;

/// <summary>
/// Limits of the supplier's service catalog (SP-02, <c>api/supplier/services</c>). Technical bounds that keep a catalog
/// readable and a request small, not product rules: they live in one place so the entity, the validation and the DTO
/// attributes cannot drift apart. A value is checked by <see cref="SupplierServiceListingRules"/> (422
/// <c>supplier_service_invalid</c>) and, for the lengths, by the request DTO attributes (400 <c>validation_error</c>).
/// </summary>
public static class SupplierServiceCatalogLimits
{
    /// <summary>
    /// Services a supplier can have, not counting the deleted ones. Checked when one is created or duplicated, under the
    /// supplier's catalog lock. A merge of duplicate profiles (<c>fix-orphaned</c>) can leave a supplier above it: the
    /// existing services stay editable, new ones wait until the count is under the limit again.
    /// </summary>
    public const int MaxServicesPerSupplier = 30;

    /// <summary>Longest name (the demo wizard counts "0/60").</summary>
    public const int NameMaxLength = 60;

    /// <summary>Longest slug: the slug of a name (at most 60 characters) plus the suffix of a collision (<c>-12</c>).</summary>
    public const int SlugMaxLength = 80;

    /// <summary>Longest category code (same column size as <c>ServiceRequest.Category</c>).</summary>
    public const int CategoryMaxLength = 100;

    /// <summary>Longest one-line summary.</summary>
    public const int SummaryMaxLength = 200;

    /// <summary>Longest description.</summary>
    public const int DescriptionMaxLength = 2000;

    /// <summary>
    /// Highest price or supplement, in cents (100,000.00 €): a guard against a typo or an overflow, not a price list.
    /// The lowest is 1 cent: "no price" is <c>null</c> with <c>requiresQuote</c>.
    /// </summary>
    public const int MaxAmountCents = 10_000_000;

    /// <summary>Shortest and longest duration of a service, in minutes (up to one day).</summary>
    public const int MinDurationMinutes = 5;

    /// <inheritdoc cref="MinDurationMinutes"/>
    public const int MaxDurationMinutes = 24 * 60;

    /// <summary>Longest minimum notice, in hours (30 days). <c>0</c> is "no notice"; <c>null</c> is the supplier's default.</summary>
    public const int MaxMinNoticeHours = 30 * 24;

    /// <summary>Supplements of one service.</summary>
    public const int MaxSupplements = 10;

    /// <summary>Longest supplement code (a stable lowercase identifier: letters, digits and hyphens).</summary>
    public const int SupplementCodeMaxLength = 40;

    /// <summary>Longest supplement label.</summary>
    public const int SupplementLabelMaxLength = 80;

    /// <summary>Highest <c>max</c> (quantity a customer can pick) of a supplement.</summary>
    public const int MaxSupplementQuantity = 99;

    /// <summary>Entries of the "included" list and of the "not included" list (each).</summary>
    public const int MaxListItems = 10;

    /// <summary>
    /// Lines the body of a request may carry for the "included" and "not included" lists before it is refused outright
    /// (400): blank and repeated lines are dropped before <see cref="MaxListItems"/> is checked.
    /// </summary>
    public const int MaxListLinesInBody = MaxListItems * 4;

    /// <summary>Longest entry of the "included" and "not included" lists.</summary>
    public const int ListItemMaxLength = 100;

    /// <summary>Highest sort position (the catalog is shown by sort position, then by creation).</summary>
    public const int MaxSortOrder = 9_999;

    /// <summary>Photos of one service.</summary>
    public const int MaxPhotos = 6;

    /// <summary>Photos accepted by one upload request (the 413 limit of the endpoint follows from it).</summary>
    public const int MaxFilesPerRequest = MaxPhotos;

    /// <summary>Largest accepted photo, in bytes (10 MB, like the other photos: <c>IImageStorageService.ValidateImage</c>).</summary>
    public const long MaxPhotoFileSizeBytes = 10 * 1024 * 1024;

    /// <summary>Upper bound of one upload request: <see cref="MaxFilesPerRequest"/> photos plus the multipart overhead.</summary>
    public const long MaxUploadRequestBytes = MaxFilesPerRequest * MaxPhotoFileSizeBytes + 1024 * 1024;

    /// <summary>Longest photo URL accepted back in an update (they are the ones the upload returned).</summary>
    public const int PhotoUrlMaxLength = 2048;
}
