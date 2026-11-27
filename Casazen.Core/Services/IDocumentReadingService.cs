namespace Casazen.Core.Services;

/// <summary>
/// The reading of the photo of an identity document (E03/F01/US01/T04, NF-40, ADR-013): the MRZ first, then the OCR. In memory only: the image
/// never touches the disk, a log or the storage (ADR-013-R6), and no value read is logged. Shared by the host wizard; the public check-in page
/// keeps its own code until E03/F04/US02/T04 moves it here.
/// </summary>
public interface IDocumentReadingService
{
    Task<DocumentReading> ReadAsync(Stream image, CancellationToken cancellationToken);
}

public enum DocumentReadingMethod
{
    None = 0,

    Mrz = 1,

    Ocr = 2,
}

public sealed record DocumentReading(
    DocumentReadingMethod Method,
    double Confidence,
    bool? CheckDigitsValid,
    bool RequiresManualReview,
    DocumentReadingFields? Fields)
{
    public const double ManualReviewBelow = 0.80;

    public static DocumentReading Nothing { get; } = new(DocumentReadingMethod.None, 0, null, true, null);
}

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
