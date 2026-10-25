namespace Casazen.Core.Services;

/// <summary>
/// OCR fallback for identity documents when MRZ is absent or unreadable (ADR-009-R22, AC-NF10-2).
/// Uses PassportEye or Tesseract (Apache-2.0) per ADR-009-R26.
/// </summary>
public interface IDocumentOcrService
{
    /// <summary>
    /// Performs OCR on an identity document image when MRZ extraction fails or is not applicable.
    /// </summary>
    /// <param name="imageStream">The document image stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Extraction result with OCR-parsed fields, or null if OCR fails.</returns>
    Task<DocumentOcrResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of OCR extraction from a document (fallback when MRZ is not available).
/// </summary>
public class DocumentOcrResult
{
    /// <summary>Full name extracted from document.</summary>
    public string? FullName { get; set; }

    /// <summary>Document number extracted.</summary>
    public string? DocumentNumber { get; set; }

    /// <summary>Date of birth extracted.</summary>
    public DateTime? DateOfBirth { get; set; }

    /// <summary>Nationality extracted.</summary>
    public string? Nationality { get; set; }

    /// <summary>Expiration date extracted.</summary>
    public DateTime? ExpirationDate { get; set; }

    /// <summary>Sex extracted.</summary>
    public string? Sex { get; set; }

    /// <summary>Confidence score (0.0 to 1.0) of the OCR read.</summary>
    public double Confidence { get; set; }

    /// <summary>Raw OCR text for manual review if confidence is low.</summary>
    public string? RawText { get; set; }
}
