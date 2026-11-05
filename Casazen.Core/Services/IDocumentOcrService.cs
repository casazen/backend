namespace Casazen.Core.Services;

/// <summary>
/// OCR fallback for identity documents when MRZ is absent or unreadable (ADR-009-R22, AC-NF10-2): it reads the visual zone, the printed
/// fields, with Tesseract (Apache-2.0, ADR-009-R26) in process and in memory only. Only a field that sits next to its own label and has a
/// plausible format is returned; nothing is guessed, and a document with nothing readable gives null (the guided manual entry applies).
/// </summary>
public interface IDocumentOcrService
{
    /// <summary>
    /// Performs OCR on an identity document image when MRZ extraction fails or is not applicable.
    /// </summary>
    /// <param name="imageStream">The document image stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Extraction result with OCR-parsed fields, or null if nothing could be read.</returns>
    Task<DocumentOcrResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of OCR extraction from a document (fallback when MRZ is not available). Fields only, never the raw text of the document.
/// </summary>
public class DocumentOcrResult
{
    /// <summary>Full name extracted from document.</summary>
    public string? FullName { get; set; }

    /// <summary>Document number extracted.</summary>
    public string? DocumentNumber { get; set; }

    /// <summary>Date of birth extracted.</summary>
    public DateTime? DateOfBirth { get; set; }

    /// <summary>Nationality extracted (3-letter code).</summary>
    public string? Nationality { get; set; }

    /// <summary>Expiration date extracted.</summary>
    public DateTime? ExpirationDate { get; set; }

    /// <summary>True when the document has expired (before today in Europe/Rome), false when it has not, null when no expiration date was read.</summary>
    public bool? IsExpired { get; set; }

    /// <summary>Date of issue extracted: a field the MRZ does not carry.</summary>
    public DateTime? DateOfIssue { get; set; }

    /// <summary>Place of issue or issuing authority extracted: a field the MRZ does not carry.</summary>
    public string? PlaceOfIssue { get; set; }

    /// <summary>Sex extracted.</summary>
    public string? Sex { get; set; }

    /// <summary>Confidence score (0.0 to 1.0) of the OCR read; always below the level that skips the guest's confirmation.</summary>
    public double Confidence { get; set; }

    /// <summary>Quality hints (<see cref="MrzQualityCodes"/>) raised by the picture; empty when none.</summary>
    public IReadOnlyList<string> Quality { get; set; } = [];
}
