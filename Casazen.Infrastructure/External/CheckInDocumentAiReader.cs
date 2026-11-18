using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Reads one identity-document photo with the configured AI provider and returns only the fields that parse.
/// The photo is not stored and is not logged. No provider, or a model that cannot read the picture: the guest types the data.
/// </summary>
public sealed class CheckInDocumentAiReader(
    IAiProvider ai,
    TimeProvider timeProvider,
    ILogger<CheckInDocumentAiReader> logger,
    IMrzExtractionService mrz,
    IDocumentOcrService ocr) : ICheckInDocumentReader
{
    private const string Prompt =
        """
        You read one identity document (passport, identity card or driving licence).
        Reply with one JSON object and no other text. Use null for any field you cannot read.
        Read the printed labels carefully, including compound names. Cross-check against the machine-readable zone when visible.
        Do not guess. Dates are yyyy-MM-dd. issuePlace is the issuing municipality or country, never the place of birth.
        Schema:
        {"firstName":null,"lastName":null,"gender":null,"dateOfBirth":null,"bornInItaly":null,"birthPlace":null,"birthProvince":null,"citizenship":null,"documentKind":null,"documentNumber":null,"issuePlace":null,"documentIssuedOn":null,"documentExpiresOn":null}
        gender is "Male" or "Female".
        bornInItaly is true only when the place of birth is in Italy.
        birthProvince is the two-letter Italian province code, or null.
        citizenship is the Italian adjective (Italiano, Francese, Tedesca), not the country name.
        documentKind is "Passport", "IdentityCard" or "DriversLicense".
        """;

    public async Task<CheckInDocumentRead> ReadAsync(Stream content, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadLimitedAsync(content, cancellationToken);
        if (bytes is null)
            return CheckInDocumentRead.Failed("too_large");

        var mediaType = MediaTypeOf(bytes);
        if (mediaType is null)
            return CheckInDocumentRead.Failed("unsupported_file");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        AiGenerationResult answer;
        try
        {
            answer = await ai.GenerateAsync(
                new AiRequest(Prompt, [new AiImage(mediaType, bytes)], Temperature: 0, MaxCompletionTokens: 400, DisableThinking: true),
                AiModelTier.Economy,
                "checkin-document",
                deadline.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Check-in document read failed ({ErrorType})", ex.GetType().Name);
            return await ReadLocalAsync(bytes, "unreadable", cancellationToken);
        }

        if (!answer.ProviderConfigured || answer.ImagesRead == 0)
            return await ReadLocalAsync(bytes, "unavailable", cancellationToken);

        var fields = CheckInDocumentFieldsParser.Parse(answer.Content, timeProvider.TodayInRomeAsDateOnly());
        if (fields?.FirstName is null || fields.LastName is null)
        {
            logger.LogInformation("Check-in document read returned no usable field (imagesRead={ImagesRead})", answer.ImagesRead);
            return await ReadLocalAsync(bytes, "unreadable", cancellationToken);
        }

        logger.LogInformation("Check-in document read returned fields");
        return new CheckInDocumentRead(true, null, fields);
    }

    private async Task<CheckInDocumentRead> ReadLocalAsync(byte[] bytes, string reason, CancellationToken ct)
    {
        static string? Day(DateTime? date) => date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        static string? Gender(string? sex) => sex switch { "M" => "Male", "F" => "Female", _ => null };

        using var stream = new MemoryStream(bytes, writable: false);
        var machine = await mrz.ExtractAsync(stream, ct);
        if (machine is { CheckDigitsValid: true, GivenNames: not null, Surname: not null })
        {
            var kind = machine.DocumentType switch { "Passport" => "Passport", "IdCard" => "IdentityCard", _ => null };
            return new(true, null, new(machine.GivenNames, machine.Surname, Gender(machine.Sex), Day(machine.DateOfBirth),
                null, null, null, machine.Nationality, kind, machine.DocumentNumber, null, null, Day(machine.ExpirationDate)));
        }

        stream.Position = 0;
        var printed = await ocr.ExtractAsync(stream, ct);
        if (printed is not { GivenNames: not null, Surname: not null })
            return CheckInDocumentRead.Failed(reason);

        return new(true, null, new(printed.GivenNames, printed.Surname, Gender(printed.Sex), Day(printed.DateOfBirth),
            null, null, null, printed.Nationality, null, printed.DocumentNumber, printed.PlaceOfIssue,
            Day(printed.DateOfIssue), Day(printed.ExpirationDate)));
    }

    private static async Task<byte[]?> ReadLimitedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > CheckInDocumentLimits.MaxImageBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>JPEG, PNG or WebP, from the file header. The declared content type is ignored.</summary>
    public static string? MediaTypeOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";
        return null;
    }
}
