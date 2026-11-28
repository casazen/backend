using System.Diagnostics;
using System.Globalization;
using System.Text;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The reading of the fields of one side of an identity document, each value bound to the zone of the format it was read from (E71/F01/US02/T01, NF-190). A small
/// interface so that the step of the flow that uses it is proved without a document and without OCR.
/// </summary>
public interface IZonedFieldReader
{
    /// <summary>
    /// The values read on <paramref name="side"/> of the card, each with the zone it comes from; never an exception: a zone that cannot be read is absent, and a
    /// timeout, a cancellation or an unavailable reader returns what was read until then (possibly nothing). <paramref name="canonicalCard"/> belongs to the caller
    /// and is not changed.
    /// </summary>
    /// <param name="canonicalCard">The card of that side, upright and cut to the card (the canonical card the geometry of the document gives), in colour.</param>
    /// <param name="format">The format of the document (the registry gives it), which says where the zones are.</param>
    /// <param name="side">The side the picture shows.</param>
    /// <param name="cancellationToken">Cancellation: it stops the reading, it is not an error.</param>
    Task<IReadOnlyList<ZonedField>> ReadAsync(RasterImage canonicalCard, DocumentFormat format, DocumentSide side, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads, on the card already set upright, the zone of every field of the side that the layout of the format places, with the OCR of the visual zone that is already
/// in use (<see cref="IPageTextReader"/>: Tesseract in the process, Italian and English), and takes a value only next to its own label, with the rule of
/// <see cref="DocumentOcrService"/> (a value without its label is not returned). On the side the MRZ is on it adds the fields of the MRZ (<see cref="IMrzExtractionService"/>,
/// ICAO 9303 with its check digits) with the zone <see cref="ZonedFieldZones.Mrz"/>. <b>Strictly local</b>: no AI provider, no HTTP client, no job; the pictures are
/// copies held in memory and zeroed before the answer.
/// <list type="bullet">
/// <item><b>Limits</b>: the whole reading takes at most <c>MrzOcr:TimeoutSeconds</c>; the concurrency is the one of the readers it uses (their pool of
/// <c>MrzOcr:MaxConcurrency</c> engines), so a reading never takes more of the machine than a document of today; the zones are read one after the other.</item>
/// <item><b>No exception to the caller</b>: an unreadable zone is absent, a timeout or a cancellation stops the reading and the fields read until then are returned,
/// an unavailable OCR library is a code in the log.</item>
/// <item><b>Privacy</b>: the log has codes and counts only (the format, the side, how many zones and fields); no value read, no text of the OCR and no message of an
/// exception (its type only); the crops and the picture handed to the MRZ reader are zeroed in <c>finally</c> (ADR-013-R2).</item>
/// <item><b>Which zones</b>: the fields of <see cref="ZonedFieldNames.All"/>; a zone of another field (the address of the card, which the verification does not
/// need) is not read: less personal data read, less OCR. Two fields that share a zone (the birth date and the birth place of the card are one line) cost one reading.</item>
/// </list>
/// </summary>
public sealed class ZonedFieldReader(
    IPageTextReader pageReader,
    IMrzExtractionService mrz,
    ILogger<ZonedFieldReader> logger,
    IOptions<MrzOcrOptions>? options = null,
    TimeProvider? timeProvider = null) : IZonedFieldReader
{
    private const double ZoneMarginShare = 0.015;

    private const int ReadingAttempts = 2;

    private static readonly string[] FiscalCodeLabels = ["CODICE FISCALE", "FISCAL CODE", "TAX CODE"];

    public async Task<IReadOnlyList<ZonedField>> ReadAsync(
        RasterImage canonicalCard, DocumentFormat format, DocumentSide side, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalCard);
        ArgumentNullException.ThrowIfNull(format);

        var fields = new List<ZonedField>();
        var sideCode = DocumentSides.CodeOf(side);
        if (!format.Layout.Sides.TryGetValue(sideCode, out var layout))
        {
            logger.LogInformation("Zoned reading: format={FormatId} side={Side} has no layout, nothing to read", format.Id, sideCode);
            return fields;
        }

        var settings = options?.Value ?? new MrzOcrOptions();
        var started = Stopwatch.GetTimestamp();
        var zones = new Dictionary<DocumentFormatRegion, ZoneCrop>();
        var mrzFields = 0;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var token = deadline.Token;

            if (string.Equals(format.Mrz.Side, sideCode, StringComparison.Ordinal))
                mrzFields = await ReadMrzAsync(canonicalCard, side, settings, fields, token);

            var today = (timeProvider ?? TimeProvider.System).TodayInRomeAsDateOnly();
            foreach (var field in layout.Fields)
            {
                if (!ZonedFieldNames.All.Contains(field.Name))
                    continue;

                token.ThrowIfCancellationRequested();
                try
                {
                    if (await ReadFieldAsync(canonicalCard, field, zones, today, token) is { } value)
                        fields.Add(new ZonedField(field.Name, value, side, field.Name, DocumentOcrService.FallbackConfidence, ZonedFieldSources.Viz));
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not DocumentReaderUnavailableException)
                {
                    logger.LogWarning("Zoned reading: a zone could not be read ({ErrorType})", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Zoned reading stopped before the end ({Reason}) with {Fields} fields read",
                cancellationToken.IsCancellationRequested ? "cancelled" : "timeout",
                fields.Count);
        }
        catch (DocumentReaderUnavailableException ex)
        {
            logger.LogWarning("Zoned reading not run to the end: the reader is not available ({Reason})", ex.ReasonCode);
        }
        catch (Exception ex)
        {
            logger.LogError("Zoned reading failed ({ErrorType}); the fields read until then are returned", ex.GetType().Name);
        }
        finally
        {
            foreach (var zone in zones.Values)
                zone.Dispose();
        }

        logger.LogInformation(
            "Zoned reading: format={FormatId} side={Side} zonesRead={Zones} fields={Fields} mrzFields={MrzFields} durationMs={DurationMs}",
            format.Id, sideCode, zones.Count, fields.Count, mrzFields, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return fields;
    }

    /// <summary>
    /// The fields of the MRZ of the card, with the confidence the MRZ reading gives them. A field whose own check digit failed is not in the reading, so it is not here. Returns how many
    /// fields it added; a reading that fails adds none and never stops the zones.
    /// </summary>
    private async Task<int> ReadMrzAsync(RasterImage card, DocumentSide side, MrzOcrOptions settings, List<ZonedField> fields, CancellationToken token)
    {
        try
        {
            var reading = await CardMrzReader.ReadCardAsync(mrz, card, settings.MaxWorkingSide, token);
            if (reading.Result is not { } result)
                return 0;

            var before = fields.Count;
            AddMrz(fields, ZonedFieldNames.Surname, result.Surname, side, result.Confidence);
            AddMrz(fields, ZonedFieldNames.GivenNames, result.GivenNames, side, result.Confidence);
            AddMrz(fields, ZonedFieldNames.BirthDate, Day(result.DateOfBirth), side, result.Confidence);
            AddMrz(fields, ZonedFieldNames.Sex, result.Sex, side, result.Confidence);
            AddMrz(fields, ZonedFieldNames.Nationality, result.Nationality, side, result.Confidence);
            AddMrz(fields, ZonedFieldNames.DocumentNumber, result.DocumentNumber, side, result.Confidence);
            AddMrz(fields, ZonedFieldNames.ExpiryDate, Day(result.ExpirationDate), side, result.Confidence);
            return fields.Count - before;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DocumentReaderUnavailableException)
        {
            logger.LogWarning("Zoned reading: the MRZ could not be read ({ErrorType})", ex.GetType().Name);
            return 0;
        }
    }

    private static void AddMrz(List<ZonedField> fields, string name, string? value, DocumentSide side, double confidence)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields.Add(new ZonedField(name, value, side, ZonedFieldZones.Mrz, confidence, ZonedFieldSources.Mrz));
    }

    private async Task<string?> ReadFieldAsync(
        RasterImage card, DocumentFormatField field, Dictionary<DocumentFormatRegion, ZoneCrop> zones, DateOnly today, CancellationToken token)
    {
        if (!zones.TryGetValue(field.Zone, out var crop))
        {
            if (Crop(card, field.Zone) is not { } image)
                return null;

            crop = new ZoneCrop(image);
            zones[field.Zone] = crop;
        }

        for (var attempt = 0; attempt < ReadingAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var text = await crop.TextAsync(attempt, pageReader, token);
            if (Extract(field.Name, text, today) is { } value)
                return value;
        }

        return null;
    }

    private static GrayBitmap? Crop(RasterImage card, DocumentFormatRegion zone)
    {
        var margin = ZoneMarginShare * Math.Min(card.Width, card.Height);
        var left = Math.Max(0, (int)Math.Floor((zone.X * card.Width) - margin));
        var top = Math.Max(0, (int)Math.Floor((zone.Y * card.Height) - margin));
        var right = Math.Min(card.Width, (int)Math.Ceiling(((zone.X + zone.W) * card.Width) + margin));
        var bottom = Math.Min(card.Height, (int)Math.Ceiling(((zone.Y + zone.H) * card.Height) + margin));
        if (right - left < 1 || bottom - top < 1)
            return null;

        using var colour = card.Cropped(left, top, right - left, bottom - top);
        return colour.ToGray();
    }

    private static string? Extract(string name, string? text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (name == ZonedFieldNames.FiscalCode)
            return FiscalCodeNextToItsLabel(text);

        if (DocumentOcrService.ParseOcrText(text, today) is not { } read)
            return null;

        return name switch
        {
            ZonedFieldNames.Surname => read.Surname,
            ZonedFieldNames.GivenNames => read.GivenNames,
            ZonedFieldNames.BirthDate => Day(read.DateOfBirth),
            ZonedFieldNames.BirthPlace => read.BirthPlace,
            ZonedFieldNames.Sex => read.Sex,
            ZonedFieldNames.Nationality => read.Nationality,
            ZonedFieldNames.DocumentNumber => read.DocumentNumber,
            ZonedFieldNames.IssueDate => Day(read.DateOfIssue),
            ZonedFieldNames.ExpiryDate => Day(read.ExpirationDate),
            _ => null,
        };
    }

    /// <summary>
    /// The fiscal code of the card: the text after the label (<c>CODICE FISCALE / FISCAL CODE</c>) on its line, or on the next, that is a valid code of a person
    /// (<see cref="ItalianFiscalCode"/>: the structure and the check character). Nothing is guessed: a code that does not check is not returned.
    /// </summary>
    private static string? FiscalCodeNextToItsLabel(string text)
    {
        var lines = text.Split('\n').Select(Plain).Where(line => line.Length > 0).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var end = -1;
            foreach (var label in FiscalCodeLabels)
            {
                var at = lines[i].LastIndexOf(label, StringComparison.Ordinal);
                if (at >= 0)
                    end = Math.Max(end, at + label.Length);
            }

            if (end < 0)
                continue;

            var rest = lines[i][end..] + (i + 1 < lines.Count ? " " + lines[i + 1] : string.Empty);
            var candidates = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Append(ItalianFiscalCode.Normalize(rest));
            foreach (var candidate in candidates)
            {
                var code = candidate.Trim(':', '/', '.', ',', ';', '|', '-');
                if (ItalianFiscalCode.IsValidPersonCode(code))
                    return code;
            }
        }

        return null;
    }

    private static string Plain(string line)
    {
        var decomposed = line.ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.IsWhiteSpace(c) ? ' ' : c);
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? Day(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class ZoneCrop(GrayBitmap image) : IDisposable
    {
        private readonly string?[] _texts = new string?[ReadingAttempts];
        private readonly bool[] _read = new bool[ReadingAttempts];

        public async Task<string?> TextAsync(int attempt, IPageTextReader reader, CancellationToken token)
        {
            if (!_read[attempt])
            {
                _texts[attempt] = attempt == 0
                    ? await reader.ReadPageAsync(image, token)
                    : await reader.ReadLayoutAsync(image, sparse: false, token);
                _read[attempt] = true;
            }

            return _texts[attempt];
        }

        public void Dispose() => image.Dispose();
    }
}
