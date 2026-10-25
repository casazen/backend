namespace Casazen.Core.Services;

/// <summary>
/// Machine Readable Zone (MRZ) extraction from identity documents per ICAO 9303
/// (ADR-009-R22, ADR-013-R10). Supports passport (TD-3), ID card (TD-1), and residence permit.
/// </summary>
public interface IMrzExtractionService
{
    /// <summary>
    /// Extracts data from the MRZ of an identity document image. Validates check digits per ICAO 9303.
    /// </summary>
    /// <param name="imageStream">The document image stream (front side).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Extraction result with parsed fields, or null if MRZ is not readable.</returns>
    Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of MRZ extraction with ICAO 9303 check digit validation (ADR-009-R23).
/// </summary>
public class MrzExtractionResult
{
    /// <summary>Document type detected (Passport, IdCard, ResidencePermit).</summary>
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>Surname (primary identifier) from MRZ.</summary>
    public string? Surname { get; set; }

    /// <summary>Given names (secondary identifier) from MRZ.</summary>
    public string? GivenNames { get; set; }

    /// <summary>Nationality (3-letter ISO 3166-1 alpha-3 code) from MRZ.</summary>
    public string? Nationality { get; set; }

    /// <summary>Document number from MRZ.</summary>
    public string? DocumentNumber { get; set; }

    /// <summary>Date of birth (YYMMDD in MRZ, parsed to DateTime) from MRZ.</summary>
    public DateTime? DateOfBirth { get; set; }

    /// <summary>Expiration date (YYMMDD in MRZ, parsed to DateTime) from MRZ.</summary>
    public DateTime? ExpirationDate { get; set; }

    /// <summary>Sex from MRZ (M, F, or X).</summary>
    public string? Sex { get; set; }

    /// <summary>Issuing state or organization (3-letter code) from MRZ.</summary>
    public string? IssuingState { get; set; }

    /// <summary>True when all check digits are valid per ICAO 9303 (ADR-009-R23).</summary>
    public bool CheckDigitsValid { get; set; }

    /// <summary>Confidence score (0.0 to 1.0) of the MRZ read. Lower confidence suggests manual review.</summary>
    public double Confidence { get; set; }
}
