using System.Globalization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The reading of the identity document the guest uploads at the start of the check-in, the prefill of <c>POST api/public/checkin/{token}/read-document</c>
/// (E70/F02/US02/T01, NF-197, ADR-013-R21, decision D5), <b>in the process only</b>: the MRZ first (<see cref="IMrzExtractionService"/>, ICAO 9303 with its
/// check digits), then the visual zone (<see cref="IDocumentOcrService"/>, a value only next to its own label), the readings the check-in already used when
/// no AI provider answered. No AI provider, no AI settings, no HTTP client: the picture never leaves the process, whatever the AI configuration of the
/// platform, and no flag sends it out again. It is held in memory only, at most <see cref="CheckInDocumentLimits.MaxImageBytes"/>, and zeroed before the
/// answer; it is not stored and not logged. The guest still confirms every field.
/// </summary>
public sealed class LocalCheckInDocumentReader(IMrzExtractionService mrz, IDocumentOcrService ocr) : ICheckInDocumentReader
{
    public async Task<CheckInDocumentRead> ReadAsync(Stream content, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = null;
        try
        {
            try
            {
                bytes = await ImageBytes.ReadAllAsync(content, CheckInDocumentLimits.MaxImageBytes, cancellationToken);
            }
            catch (InvalidDataException)
            {
                return CheckInDocumentRead.Failed("too_large");
            }

            // An empty upload answers like an oversized one, as it always did (the endpoint refuses an empty file before asking).
            if (bytes.Length == 0)
                return CheckInDocumentRead.Failed("too_large");
            if (!IsSupportedPicture(bytes))
                return CheckInDocumentRead.Failed("unsupported_file");

            return await ReadLocalAsync(bytes, cancellationToken);
        }
        finally
        {
            ImageBytes.Zero(bytes);
        }
    }

    /// <summary>
    /// The MRZ, then the visual zone of the same picture. Fields the MRZ proved (check digits valid) win and the printed ones complete them; printed fields
    /// alone count only when the picture shows a document and its names; otherwise the reason says why nothing was read.
    /// </summary>
    private async Task<CheckInDocumentRead> ReadLocalAsync(byte[] bytes, CancellationToken ct)
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
                return new(true, null, checkedFields, checkedSide);
            return CheckInDocumentRead.Failed(observation.Failure ?? (printed?.HasDocumentEvidence == true ? "unreadable" : "not_document"));
        }

        var visual = new CheckInDocumentFields(printed.GivenNames, printed.Surname, Gender(printed.Sex), Day(printed.DateOfBirth),
            printed.BirthProvince is not null ? true : null, printed.BirthPlace, printed.BirthProvince, printed.Nationality,
            printed.DocumentKind, printed.DocumentNumber, printed.PlaceOfIssue, Day(printed.DateOfIssue), Day(printed.ExpirationDate),
            printed.ResidenceStreet, printed.ResidencePostalCode, printed.ResidenceCityName, printed.ResidenceProvince,
            printed.ResidenceProvince is not null ? "ITALIA" : null, printed.ResidenceProvince is not null ? true : null);
        return checkedFields is null
            ? new(true, null, visual, printed.DocumentKind switch { "IdentityCard" or "DriversLicense" => "front", "Passport" => "single", _ => null })
            : new(true, null, Supplement(checkedFields, visual), checkedSide);
    }

    /// <summary>The fields of <paramref name="primary"/>, with the ones it misses taken from <paramref name="other"/>.</summary>
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

    /// <summary>JPEG, PNG or WebP, from the file header (the declared content type is ignored), as the endpoint always accepted.</summary>
    private static bool IsSupportedPicture(ReadOnlySpan<byte> bytes) =>
        (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        || (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        || (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P');
}
