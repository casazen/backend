using System.Globalization;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The reading of the identity document the guest uploads at the start of the check-in, the prefill of <c>POST api/public/checkin/{token}/read-document</c>
/// (E70/F02/US02/T01, NF-197, ADR-013-R21, decision D5), <b>in the process only</b>: the MRZ first (<see cref="IMrzExtractionService"/>, ICAO 9303 with its
/// check digits), then the visual zone (<see cref="IDocumentOcrService"/>, a value only next to its own label), the readings the check-in already used when
/// no AI provider answered. No AI provider, no AI settings, no HTTP client: the picture never leaves the process, whatever the AI configuration of the
/// platform, and no flag sends it out again. It is held in memory only, at most <see cref="CheckInDocumentLimits.MaxImageBytes"/>, and zeroed before the
/// answer; it is not stored and not logged. The guest still confirms every field.
/// <para>
/// <b>Zoned prefill</b> (<see cref="ReadZonedAsync"/>, E71/F01/US03/T01, NF-190, only asked for with the self-hosted flow on): the same reading, and on top of it the values read by
/// the zones of the card, each with its side, its zone, its confidence and its source, and the fields of the check-in the guest has still to type. The card is found and set upright
/// by the geometry of the document (<see cref="DocumentPictureAnalyzer"/>); when it cannot be, the answer is the reading of today and no zone is read. The services of the zones are
/// taken from the container only when asked for, so the reading of today needs none of them.
/// </para>
/// </summary>
public sealed class LocalCheckInDocumentReader(
    IMrzExtractionService mrz,
    IDocumentOcrService ocr,
    ZonedPrefillServices? zonedServices = null,
    ILogger<LocalCheckInDocumentReader>? logger = null) : ICheckInDocumentReader
{
    public async Task<CheckInDocumentRead> ReadAsync(Stream content, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = null;
        try
        {
            CheckInDocumentRead? failure;
            (bytes, failure) = await ReadBytesAsync(content, cancellationToken);
            if (failure is not null)
                return failure;

            return (await ReadLocalAsync(bytes!, cancellationToken)).Read;
        }
        finally
        {
            ImageBytes.Zero(bytes);
        }
    }

    public async Task<CheckInZonedRead> ReadZonedAsync(Stream content, bool lookForPortrait, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = null;
        try
        {
            CheckInDocumentRead? failure;
            (bytes, failure) = await ReadBytesAsync(content, cancellationToken);
            if (failure is not null)
                return CheckInZonedRead.Of(failure);

            var (read, machine) = await ReadLocalAsync(bytes!, cancellationToken);

            IReadOnlyList<ZonedField> fields = read.Read ? await ReadZonesAsync(bytes!, machine, lookForPortrait, cancellationToken) : [];
            return new CheckInZonedRead(read, fields, CheckInPrefillFields.MissingFrom(read.Fields, fields));
        }
        finally
        {
            ImageBytes.Zero(bytes);
        }
    }

    private static async Task<(byte[]? Bytes, CheckInDocumentRead? Failure)> ReadBytesAsync(Stream content, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await ImageBytes.ReadAllAsync(content, CheckInDocumentLimits.MaxImageBytes, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return (null, CheckInDocumentRead.Failed("too_large"));
        }

        if (bytes.Length == 0)
            return (bytes, CheckInDocumentRead.Failed("too_large"));
        if (!IsSupportedPicture(bytes))
            return (bytes, CheckInDocumentRead.Failed("unsupported_file"));

        return (bytes, null);
    }

    /// <summary>
    /// The MRZ, then the visual zone of the same picture. Fields the MRZ proved (check digits valid) win and the printed ones complete them; printed fields
    /// alone count only when the picture shows a document and its names; otherwise the reason says why nothing was read. The MRZ that was read comes with the
    /// answer (null when none): the zoned prefill tells the format of the document from it.
    /// </summary>
    private async Task<(CheckInDocumentRead Read, MrzExtractionResult? Machine)> ReadLocalAsync(byte[] bytes, CancellationToken ct)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var machine = await mrz.ExtractAsync(stream, ct);
        CheckInDocumentFields? checkedFields = null;
        if (machine is { CheckDigitsValid: true, GivenNames: not null, Surname: not null })
        {
            var kind = machine.DocumentType switch { "Passport" => "Passport", "IdCard" => "IdentityCard", _ => null };
            checkedFields = new(machine.GivenNames, machine.Surname, Gender(machine.Sex), Day(machine.DateOfBirth),
                null, null, null, machine.Nationality, kind, machine.DocumentNumber, null, null, Day(machine.ExpirationDate));
        }

        stream.Position = 0;
        var observation = await ocr.ObserveAsync(stream, ct);
        var printed = observation.Fields;
        var checkedSide = machine switch
        {
            { DocumentType: "Passport" } => "single",
            { DocumentType: "IdCard", IssuingState: "ITA" } => "back",
            _ => null,
        };
        if (printed is null || (checkedFields is null && printed is not { HasDocumentEvidence: true, GivenNames: not null, Surname: not null }))
        {
            if (checkedFields is not null)
                return (new(true, null, checkedFields, checkedSide), machine);
            return (CheckInDocumentRead.Failed(observation.Failure ?? (printed?.HasDocumentEvidence == true ? "unreadable" : "not_document")), machine);
        }

        var visual = new CheckInDocumentFields(printed.GivenNames, printed.Surname, Gender(printed.Sex), Day(printed.DateOfBirth),
            printed.BirthProvince is not null ? true : null, printed.BirthPlace, printed.BirthProvince, printed.Nationality,
            printed.DocumentKind, printed.DocumentNumber, printed.PlaceOfIssue, Day(printed.DateOfIssue), Day(printed.ExpirationDate),
            printed.ResidenceStreet, printed.ResidencePostalCode, printed.ResidenceCityName, printed.ResidenceProvince,
            printed.ResidenceProvince is not null ? "ITALIA" : null, printed.ResidenceProvince is not null ? true : null);
        return checkedFields is null
            ? (new(true, null, visual, printed.DocumentKind switch { "IdentityCard" or "DriversLicense" => "front", "Passport" => "single", _ => null }), machine)
            : (new(true, null, Supplement(checkedFields, visual), checkedSide), machine);
    }

    /// <summary>
    /// The values read by the zones of the card of the photo. The format is the one the MRZ of the photo shows, or the card the registry holds when it shows none; the side is not told by
    /// the prefill, so the MRZ says the side that has it and, without one, the side with the portrait is taken, and the geometry confirms it from the picture itself. Empty when the
    /// services of the zones are not there, the card cannot be found whole and upright, or the models are not available: the reading of today stands, a guest types what is not read.
    /// <para>
    /// <paramref name="lookForPortrait"/> false (no current consent, a minor, a refusal): the face detector is never asked. The card is judged and turned by its box and its MRZ band, and the
    /// side is the one the MRZ or the layout names, with nothing to confirm it from the picture: a picture of the other side has no label next to a value, so it gives nothing.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ZonedField>> ReadZonesAsync(byte[] bytes, MrzExtractionResult? machine, bool lookForPortrait, CancellationToken ct)
    {
        try
        {
            if (zonedServices?.Pictures is not { } pictures || zonedServices.Formats is not { } formats || zonedServices.Sides is not { } sides)
                return [];

            var format = machine is null
                ? DocumentPictureAnalyzer.DefaultCardFormat(formats.All)
                : formats.DetectFromMrz(machine.DocumentCode, machine.IssuingState, machine.MrzType);
            if (format is null)
                return [];

            DocumentSide declared;
            if (machine is not null && DocumentSides.TryParse(format.Mrz.Side, out var mrzSide))
                declared = mrzSide;
            else
                declared = DocumentPictureAnalyzer.PortraitSideOf(format) ?? DocumentSide.Front;

            DocumentPicture picture;
            try
            {
                picture = pictures.Open(bytes);
            }
            catch (InvalidDataException)
            {
                return [];
            }

            using (picture)
            {
                var report = picture.Inspect(format, declared, lookForPortrait, ct);
                var shown = lookForPortrait ? sides.Classify(report, machine is not null, format) : declared;
                if (shown is not { } side)
                    return [];

                return await picture.ReadFieldsAsync(format, side, report, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("Zoned prefill of a document not run ({ErrorType})", ex.GetType().Name);
            return [];
        }
    }

    private static CheckInDocumentFields Supplement(CheckInDocumentFields primary, CheckInDocumentFields other) => primary with
    {
        ResidenceStreet = primary.ResidenceStreet ?? other.ResidenceStreet,
        ResidencePostalCode = primary.ResidencePostalCode ?? other.ResidencePostalCode,
        ResidenceCityName = primary.ResidenceCityName ?? other.ResidenceCityName,
        ResidenceProvince = primary.ResidenceProvince ?? other.ResidenceProvince,
        ResidenceCountryName = primary.ResidenceCountryName ?? other.ResidenceCountryName,
        ResidenceInItaly = primary.ResidenceInItaly ?? other.ResidenceInItaly,
        Gender = primary.Gender ?? other.Gender,
        DateOfBirth = primary.DateOfBirth ?? other.DateOfBirth,
        BornInItaly = primary.BornInItaly ?? other.BornInItaly,
        BirthPlace = primary.BirthPlace ?? other.BirthPlace,
        BirthProvince = primary.BirthProvince ?? other.BirthProvince,
        Citizenship = primary.Citizenship ?? other.Citizenship,
        DocumentNumber = primary.DocumentNumber ?? other.DocumentNumber,
        IssuePlace = primary.IssuePlace ?? other.IssuePlace,
        DocumentIssuedOn = primary.DocumentIssuedOn ?? other.DocumentIssuedOn,
        DocumentExpiresOn = primary.DocumentExpiresOn ?? other.DocumentExpiresOn,
    };

    private static string? Day(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Gender(string? sex) => sex switch { "M" => "Male", "F" => "Female", _ => null };

    private static bool IsSupportedPicture(ReadOnlySpan<byte> bytes) =>
        (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        || (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        || (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P');
}
