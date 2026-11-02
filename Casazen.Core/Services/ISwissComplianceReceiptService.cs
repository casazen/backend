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
    /// are kept, the file itself is read to be hashed and is not stored. The receipt belongs to the org of the property (never to
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
}

/// <summary>What a Swiss compliance receipt is about (ADR-016); stored in <see cref="SwissComplianceReceipt.Type"/>.</summary>
public enum SwissComplianceFileType
{
    /// <summary>The Ticino police notification: the XML of one night.</summary>
    Police,

    /// <summary>The OTR overnight report: the CSV of one period.</summary>
    Otr,
}

/// <summary>The values of <see cref="SwissComplianceReceipt.Type"/> and the limits of a receipt.</summary>
public static class SwissComplianceReceipts
{
    /// <summary>Largest receipt file accepted (a confirmation page or PDF of the portal is far smaller).</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>Same limit as <see cref="SwissComplianceReceipt.Reference"/>.</summary>
    public const int MaxReferenceLength = 200;

    /// <summary>The text stored in <see cref="SwissComplianceReceipt.Type"/>: <c>Police</c> or <c>OTR</c>.</summary>
    public static string StoredType(SwissComplianceFileType type) => type switch
    {
        SwissComplianceFileType.Police => "Police",
        SwissComplianceFileType.Otr => "OTR",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown Swiss compliance file type"),
    };
}
