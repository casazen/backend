using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Archive of the receipts of the Swiss compliance files (NF-15, ADR-016, AC-NF15-3): after the host submitted a police
/// notification (<see cref="ITicinoNotificationService"/>) or an OTR report (<see cref="IOtrTaxService"/>) on the cantonal
/// portal, it records the receipt the portal gave back.
/// </summary>
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

    /// <summary>Records and retains the original receipt, including its download name and content type.</summary>
    Task<SwissComplianceReceipt> RecordAsync(Guid propertyId, SwissComplianceFileType type, DateTime reportDate,
        string reference, Stream receipt, string fileName, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Receipt metadata of one authorized property. Old hash-only receipts remain listed with HasFile=false.</summary>
    Task<SwissComplianceReceiptPage> ListAsync(Guid propertyId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);

    /// <summary>The original bytes of a receipt, through tenant-scoped private storage; never an object-store URL.</summary>
    Task<SwissComplianceReceiptFile> OpenFileAsync(Guid propertyId, Guid receiptId, CancellationToken cancellationToken = default);
}

public sealed record SwissComplianceReceiptItem(SwissComplianceReceipt Receipt, bool HasFile);
public sealed record SwissComplianceReceiptPage(IReadOnlyList<SwissComplianceReceiptItem> Items, int Page, int PageSize, int TotalCount, bool IsTicinoProperty);
public sealed record SwissComplianceReceiptFile(Stream Content, string FileName, string ContentType);

/// <summary>
/// What a Swiss compliance receipt is about (ADR-016); stored in <see cref="SwissComplianceReceipt.Type"/> as the text of
/// <see cref="SwissComplianceReceipts.StoredType"/>. Values are appended, never renumbered.
/// </summary>
public enum SwissComplianceFileType
{
    /// <summary>The Ticino police notification: the receipt of the portal for the file of one arrival day.</summary>
    Police,

    /// <summary>The OTR overnight report: the CSV of one period.</summary>
    Otr,

    /// <summary>
    /// The written authorization of the parents of a minor who stays without an adult (decision D11, casazen/backend#979): kept five years; the day of
    /// the police notification asks for it (<c>parental_authorization_needed</c>). Report date = the arrival day.
    /// </summary>
    ParentalAuthorization,

    /// <summary>
    /// The sheet of a group of six or more persons (decision D11): kept five years at the disposal of the police and the regional tourism organizations.
    /// Report date = the arrival day.
    /// </summary>
    GroupSheet,
}

/// <summary>The values of <see cref="SwissComplianceReceipt.Type"/> and the limits of a receipt.</summary>
public static class SwissComplianceReceipts
{
    /// <summary>Largest receipt file accepted (a confirmation page or PDF of the portal is far smaller).</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>Same limit as <see cref="SwissComplianceReceipt.Reference"/>.</summary>
    public const int MaxReferenceLength = 200;

    /// <summary>Years a document of the police rules is kept from its date (NF-128): the police receipts, the group sheets, the parents' authorizations.</summary>
    public const int PoliceRetentionYears = 5;

    /// <summary>
    /// The text stored in <see cref="SwissComplianceReceipt.Type"/>: <c>Police</c>, <c>OTR</c>, <c>ParentalAuthorization</c> or <c>GroupSheet</c> (at most
    /// <see cref="SwissComplianceReceipt.TypeMaxLength"/> characters).
    /// </summary>
    public static string StoredType(SwissComplianceFileType type) => type switch
    {
        SwissComplianceFileType.Police => "Police",
        SwissComplianceFileType.Otr => "OTR",
        SwissComplianceFileType.ParentalAuthorization => "ParentalAuthorization",
        SwissComplianceFileType.GroupSheet => "GroupSheet",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown Swiss compliance file type"),
    };

    /// <summary>
    /// The types kept <see cref="PoliceRetentionYears"/> years and then deleted (NF-128): every type but the OTR report, which is tax evidence with no
    /// retention rule decided yet (it is kept).
    /// </summary>
    public static IReadOnlyList<string> RetainedFiveYears { get; } =
    [
        StoredType(SwissComplianceFileType.Police),
        StoredType(SwissComplianceFileType.ParentalAuthorization),
        StoredType(SwissComplianceFileType.GroupSheet),
    ];

    /// <summary>
    /// The cut day of the retention (NF-128): <paramref name="today"/> minus <see cref="PoliceRetentionYears"/> years. The daily job deletes a document of
    /// <see cref="RetainedFiveYears"/> whose report date is before it, and one dated on it is kept; the one definition of the cut, for the job and for the upload.
    /// </summary>
    public static DateOnly RetentionCutoffDay(DateOnly today) => today.AddYears(-PoliceRetentionYears);

    /// <summary>
    /// True when a document of <paramref name="type"/> dated <paramref name="reportDay"/> is already past its retention on <paramref name="today"/>: the type is kept
    /// five years and the date is before <see cref="RetentionCutoffDay"/>, so the daily job would delete it the night it is recorded. Such a document is refused at
    /// the upload (a legal proof is never destroyed in silence); an OTR report is never past its retention.
    /// </summary>
    public static bool IsPastRetention(SwissComplianceFileType type, DateOnly reportDay, DateOnly today) =>
        RetainedFiveYears.Contains(StoredType(type)) && reportDay < RetentionCutoffDay(today);
}
