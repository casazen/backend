namespace Casazen.Core.Suppliers;

public static class ServiceRequestLimits
{
    public const int CancellationReasonMaxLength = 500;

    public const int CompletionNotesMaxLength = 1000;

    public const int ProposalMessageMaxLength = 500;

    public const int ServiceNameMaxLength = SupplierServiceCatalogLimits.NameMaxLength;

    public const int MaxAmountCents = SupplierServiceCatalogLimits.MaxAmountCents;

    public const int MaxExtras = 10;

    public const int ExtraLabelMaxLength = 80;

    public const int MaxWorkPhotos = 6;

    public const int MaxFilesPerRequest = MaxWorkPhotos;

    public const long MaxPhotoFileSizeBytes = SupplierServiceCatalogLimits.MaxPhotoFileSizeBytes;

    public const long MaxUploadRequestBytes = MaxFilesPerRequest * MaxPhotoFileSizeBytes + 1024 * 1024;

    public const int MaxBatchAccept = 20;

    public const int MaxQuoteLines = 20;

    public const int QuoteLineLabelMaxLength = 120;

    public const int QuoteNoteMaxLength = 500;

    public const int MaxQuoteEvents = 30;

    public const int InboxFilterMaxLength = 100;
}
