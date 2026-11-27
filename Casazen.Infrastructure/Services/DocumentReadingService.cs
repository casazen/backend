using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// A stream that cannot be rewound is copied into memory (never a file) so the OCR can read it after the MRZ. The log says the method and the
/// confidence only, never a value read.
/// </remarks>
public sealed class DocumentReadingService(
    IMrzExtractionService mrz,
    IDocumentOcrService ocr,
    ILogger<DocumentReadingService> logger) : IDocumentReadingService
{
    public async Task<DocumentReading> ReadAsync(Stream image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);

        MemoryStream? copy = null;
        try
        {
            var source = image;
            if (!image.CanSeek)
            {
                copy = new MemoryStream();
                await image.CopyToAsync(copy, cancellationToken);
                copy.Position = 0;
                source = copy;
            }

            var start = source.Position;
            if (await mrz.ExtractAsync(source, cancellationToken) is { } fromMrz)
            {
                logger.LogInformation("Document read from the MRZ, confidence {Confidence}", fromMrz.Confidence);
                return new DocumentReading(
                    DocumentReadingMethod.Mrz,
                    fromMrz.Confidence,
                    fromMrz.CheckDigitsValid,
                    !fromMrz.CheckDigitsValid || fromMrz.Confidence < DocumentReading.ManualReviewBelow,
                    new DocumentReadingFields(
                        fromMrz.DocumentType,
                        fromMrz.Surname,
                        fromMrz.GivenNames,
                        null,
                        fromMrz.DocumentNumber,
                        DateOf(fromMrz.DateOfBirth),
                        fromMrz.Nationality,
                        DateOf(fromMrz.ExpirationDate),
                        fromMrz.Sex,
                        fromMrz.IssuingState));
            }

            source.Position = start;
            if (await ocr.ExtractAsync(source, cancellationToken) is { } fromOcr)
            {
                logger.LogInformation("Document read by OCR, confidence {Confidence}", fromOcr.Confidence);
                return new DocumentReading(
                    DocumentReadingMethod.Ocr,
                    fromOcr.Confidence,
                    null,
                    fromOcr.Confidence < DocumentReading.ManualReviewBelow,
                    new DocumentReadingFields(
                        null,
                        null,
                        null,
                        fromOcr.FullName,
                        fromOcr.DocumentNumber,
                        DateOf(fromOcr.DateOfBirth),
                        fromOcr.Nationality,
                        DateOf(fromOcr.ExpirationDate),
                        fromOcr.Sex,
                        null));
            }

            logger.LogInformation("Document not read (no MRZ, no OCR result): the host types the data");
            return DocumentReading.Nothing;
        }
        finally
        {
            if (copy is not null)
                await copy.DisposeAsync();
        }
    }

    private static DateOnly? DateOf(DateTime? value) => value is { } date ? DateOnly.FromDateTime(date) : null;
}
