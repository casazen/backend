namespace Casazen.Core.Services;

/// <summary>
/// Machine Readable Zone (MRZ) extraction from identity documents per ICAO 9303
/// (ADR-009-R22, ADR-013-R10). Supports passport (TD-3), ID card (TD-1), and residence permit (TD-2).
/// The picture is processed in memory only: no temporary file, no copy that outlives the call (ADR-013-R6). No MRZ text, name, number or date
/// is ever logged.
/// </summary>
public interface IMrzExtractionService
{
    /// <summary>
    /// Extracts data from the MRZ of an identity document image. Validates check digits per ICAO 9303.
    /// </summary>
    /// <param name="imageStream">The document image stream (front side).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Extraction result with parsed fields, or null if MRZ is not readable (the guided manual entry then applies).</returns>
    Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same reading as <see cref="ExtractAsync"/>, plus the quality hints even when nothing could be read (for the verification agent, which
    /// turns them into guidance codes: <c>mrz_not_found</c>, <c>glare</c>, <c>blur</c>).
    /// </summary>
    Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default);
}

/// <summary>What a reading of the MRZ found: the result (null when unreadable) and the quality hints of the picture, codes only.</summary>
/// <param name="Result">The parsed MRZ, or null when it was not found or not trustworthy.</param>
/// <param name="Quality">Codes from <see cref="MrzQualityCodes"/>; empty when the picture raised none.</param>
public sealed record MrzReading(MrzExtractionResult? Result, IReadOnlyList<string> Quality);

/// <summary>Quality hints of a document picture (codes only), turned into guidance by the verification agent.</summary>
public static class MrzQualityCodes
{
    /// <summary>No MRZ band was found in the picture (or in any of its rotations).</summary>
    public const string MrzNotFound = "mrz_not_found";

    /// <summary>A large share of the MRZ band is saturated: a reflection hides the text.</summary>
    public const string Glare = "glare";

    /// <summary>The MRZ band is out of focus or the hand moved.</summary>
    public const string Blur = "blur";
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

    /// <summary>Document number from MRZ. Null when its own check digit failed.</summary>
    public string? DocumentNumber { get; set; }

    /// <summary>Date of birth (YYMMDD in MRZ, parsed to DateTime) from MRZ. Null when its own check digit failed.</summary>
    public DateTime? DateOfBirth { get; set; }

    /// <summary>Expiration date (YYMMDD in MRZ, parsed to DateTime) from MRZ. Null when its own check digit failed.</summary>
    public DateTime? ExpirationDate { get; set; }

    /// <summary>
    /// True when the document has expired (its expiration date is before today in Europe/Rome), false when it is still valid, null when the
    /// expiration date could not be read reliably. An expired document is not accepted by the verification agent.
    /// </summary>
    public bool? IsExpired { get; set; }

    /// <summary>Sex from MRZ (M, F, or X).</summary>
    public string? Sex { get; set; }

    /// <summary>Issuing state or organization (3-letter code) from MRZ.</summary>
    public string? IssuingState { get; set; }

    /// <summary>
    /// True when every check digit is valid per ICAO 9303 (ADR-009-R23): the three of the fields, the one of the optional data when it is used
    /// and the composite one. A reading with a failed digit is never presented as certain.
    /// </summary>
    public bool CheckDigitsValid { get; set; }

    /// <summary>Confidence score (0.0 to 1.0) of the MRZ read. Lower confidence suggests manual review; low (0.4) when any check digit fails.</summary>
    public double Confidence { get; set; }

    /// <summary>Quality hints (<see cref="MrzQualityCodes"/>) raised by the picture the MRZ was read from; empty when none.</summary>
    public IReadOnlyList<string> Quality { get; set; } = [];
}
