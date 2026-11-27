namespace Casazen.Core.Services;

/// <summary>
/// The reading of the photo of an identity document (E03/F01/US01/T04, NF-40, ADR-013): the MRZ first, then the OCR. In memory only: the image
/// never touches the disk, a log or the storage (ADR-013-R6), and no value read is logged. Shared by the host wizard; the public check-in page
/// keeps its own code until E03/F04/US02/T04 moves it here.
/// </summary>
public interface IDocumentReadingService
{
    /// <summary>Reads <paramref name="image"/> (JPEG or PNG); the stream is the caller's and is not kept.</summary>
    Task<DocumentReading> ReadAsync(Stream image, CancellationToken cancellationToken);
}

/// <summary>How the fields were read.</summary>
public enum DocumentReadingMethod
{
    /// <summary>Nothing could be read: the host types the data.</summary>
    None = 0,

    /// <summary>The machine-readable zone, with its check digits.</summary>
    Mrz = 1,

    /// <summary>The printed text, when there is no readable MRZ.</summary>
    Ocr = 2,
}

/// <summary>The outcome of a reading.</summary>
/// <param name="Method">MRZ, OCR or none.</param>
/// <param name="Confidence">0 to 1.</param>
/// <param name="CheckDigitsValid">The check digits of the MRZ; null for the OCR or no reading.</param>
/// <param name="RequiresManualReview">The check digits are invalid, the confidence is under <see cref="ManualReviewBelow"/> or nothing was read.</param>
/// <param name="Fields">The fields read; null when nothing was.</param>
public sealed record DocumentReading(
    DocumentReadingMethod Method,
    double Confidence,
    bool? CheckDigitsValid,
    bool RequiresManualReview,
    DocumentReadingFields? Fields)
{
    /// <summary>Under this confidence the host reviews every field.</summary>
    public const double ManualReviewBelow = 0.80;

    /// <summary>Nothing could be read.</summary>
    public static DocumentReading Nothing { get; } = new(DocumentReadingMethod.None, 0, null, true, null);
}

/// <summary>The fields read on a document. Never logged.</summary>
public sealed record DocumentReadingFields(
    string? DocumentType,
    string? Surname,
    string? GivenNames,
    string? FullName,
    string? DocumentNumber,
    DateOnly? DateOfBirth,
    string? Nationality,
    DateOnly? ExpirationDate,
    string? Sex,
    string? IssuingState);
