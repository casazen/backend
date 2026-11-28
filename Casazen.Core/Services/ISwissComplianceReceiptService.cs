using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface ISwissComplianceReceiptService
{
    /// <summary>
    /// Records the receipt of a submission of a Ticino property: the SHA-256 of the file and the reference chosen by the host
    /// are kept and the file is encrypted in private storage. The receipt belongs to the org of the property (never to
    /// one the caller names).
    /// </summary>
    /// <param name="propertyId">Property in canton Ticino (TI).</param>
    /// <param name="type">What was submitted.</param>
    /// <param name="reportDate">Day the report is about (police notification) or first day of the period (OTR report).</param>
    /// <param name="reference">Reference of the submission: the portal's confirmation number or a reference of the host's.</param>
    /// <param name="receipt">The receipt file, read from its start.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored receipt.</returns>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">No such property in the caller's org (<c>property_not_found</c>).</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">The property is not in canton Ticino (<c>property_not_in_ticino</c>).</exception>
    Task<SwissComplianceReceipt> RecordAsync(
        Guid propertyId,
        SwissComplianceFileType type,
        DateTime reportDate,
        string reference,
        Stream receipt,
        CancellationToken cancellationToken = default);

    Task<SwissComplianceReceipt> RecordAsync(Guid propertyId, SwissComplianceFileType type, DateTime reportDate,
        string reference, Stream receipt, string fileName, string contentType, CancellationToken cancellationToken = default);

    Task<SwissComplianceReceiptPage> ListAsync(Guid propertyId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);

    Task<SwissComplianceReceiptFile> OpenFileAsync(Guid propertyId, Guid receiptId, CancellationToken cancellationToken = default);
}

public sealed record SwissComplianceReceiptItem(SwissComplianceReceipt Receipt, bool HasFile);
public sealed record SwissComplianceReceiptPage(IReadOnlyList<SwissComplianceReceiptItem> Items, int Page, int PageSize, int TotalCount, bool IsTicinoProperty);
public sealed record SwissComplianceReceiptFile(Stream Content, string FileName, string ContentType);

public enum SwissComplianceFileType
{
    Police,

    Otr,

    ParentalAuthorization,

    GroupSheet,
}

public static class SwissComplianceReceipts
{
    public const long MaxFileBytes = 10 * 1024 * 1024;

    public const int MaxReferenceLength = 200;

    public const int PoliceRetentionYears = 5;

    public static string StoredType(SwissComplianceFileType type) => type switch
    {
        SwissComplianceFileType.Police => "Police",
        SwissComplianceFileType.Otr => "OTR",
        SwissComplianceFileType.ParentalAuthorization => "ParentalAuthorization",
        SwissComplianceFileType.GroupSheet => "GroupSheet",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown Swiss compliance file type"),
    };

    public static IReadOnlyList<string> RetainedFiveYears { get; } =
    [
        StoredType(SwissComplianceFileType.Police),
        StoredType(SwissComplianceFileType.ParentalAuthorization),
        StoredType(SwissComplianceFileType.GroupSheet),
    ];

    public static DateOnly RetentionCutoffDay(DateOnly today) => today.AddYears(-PoliceRetentionYears);

    /// <summary>
    /// True when a document of <paramref name="type"/> dated <paramref name="reportDay"/> is already past its retention on <paramref name="today"/>: the type is kept
    /// five years and the date is before <see cref="RetentionCutoffDay"/>, so the daily job would delete it the night it is recorded. Such a document is refused at
    /// the upload (a legal proof is never destroyed in silence); an OTR report is never past its retention.
    /// </summary>
    public static bool IsPastRetention(SwissComplianceFileType type, DateOnly reportDay, DateOnly today) =>
        RetainedFiveYears.Contains(StoredType(type)) && reportDay < RetentionCutoffDay(today);
}
