namespace Casazen.Core.Suppliers;

public static class SupplierServiceCatalogLimits
{
    /// <summary>
    /// Services a supplier can have, not counting the deleted ones. Checked when one is created or duplicated, under the
    /// supplier's catalog lock. A merge of duplicate profiles (<c>fix-orphaned</c>) can leave a supplier above it: the
    /// existing services stay editable, new ones wait until the count is under the limit again.
    /// </summary>
    public const int MaxServicesPerSupplier = 30;

    public const int NameMaxLength = 60;

    public const int SlugMaxLength = 80;

    public const int CategoryMaxLength = 100;

    public const int SummaryMaxLength = 200;

    public const int DescriptionMaxLength = 2000;

    public const int MaxAmountCents = 10_000_000;

    public const int MinDurationMinutes = 5;

    public const int MaxDurationMinutes = 24 * 60;

    public const int MaxMinNoticeHours = 30 * 24;

    public const int MaxSupplements = 10;

    public const int SupplementCodeMaxLength = 40;

    public const int SupplementLabelMaxLength = 80;

    public const int MaxSupplementQuantity = 99;

    public const int MaxListItems = 10;

    public const int MaxListLinesInBody = MaxListItems * 4;

    public const int ListItemMaxLength = 100;

    public const int MaxSortOrder = 9_999;

    public const int MaxPhotos = 6;

    public const int MaxFilesPerRequest = MaxPhotos;

    public const long MaxPhotoFileSizeBytes = 10 * 1024 * 1024;

    public const long MaxUploadRequestBytes = MaxFilesPerRequest * MaxPhotoFileSizeBytes + 1024 * 1024;

    public const int PhotoUrlMaxLength = 2048;
}
