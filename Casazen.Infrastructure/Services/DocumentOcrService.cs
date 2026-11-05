using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// OCR fallback for identity documents when MRZ is absent or unreadable (ADR-009-R22, AC-NF10-2): it reads the visual zone, the printed
/// fields, and so also the two the MRZ does not carry, the place and the date of issue (E03/F04/US01/T02). Tesseract (Apache-2.0, ADR-009-R26)
/// in process, with the Italian and English language files, from memory: the picture never touches the disk (ADR-013-R6) and is zeroed before
/// returning. <b>Nothing is guessed</b>: a value is taken only when it sits next to its own label (in Italian, English, French, German or
/// Spanish) and has a plausible format; a document with nothing readable gives null and the guided manual entry applies. No text of the
/// document is logged and the raw text is not returned.
/// </summary>
public class DocumentOcrService(
    ILogger<DocumentOcrService> logger,
    IPageTextReader? pageReader = null,
    IOptions<MrzOcrOptions>? options = null,
    TimeProvider? timeProvider = null) : IDocumentOcrService
{
    /// <summary>The fallback is never presented as certain: the guest confirms every field (the check-in page asks for review under 0.80).</summary>
    internal const double FallbackConfidence = 0.55;

    /// <summary>
    /// Performs OCR on an identity document image when MRZ extraction fails or is not applicable.
    /// Uses Tesseract (Apache-2.0) for text extraction.
    /// </summary>
    public async Task<DocumentOcrResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        var settings = options?.Value ?? new MrzOcrOptions();
        var started = Stopwatch.GetTimestamp();
        byte[]? bytes = null;
        RasterImage? colour = null;
        GrayBitmap? page = null;
        GrayBitmap? scaled = null;
        try
        {
            if (pageReader is null)
            {
                logger.LogInformation("Document OCR not run: the document reader is not configured");
                return null;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var token = deadline.Token;

            bytes = await ImageBytes.ReadAllAsync(imageStream, settings.MaxImageBytes, token);
            colour = RasterImage.Decode(bytes, settings.MaxImagePixels);
            ImageBytes.Zero(bytes);
            page = colour.ToGray();
            colour.Dispose();

            var longest = Math.Max(page.Width, page.Height);
            if (longest > settings.MaxWorkingSide)
            {
                scaled = page.Resized(
                    Math.Max(1, (int)Math.Round((double)page.Width * settings.MaxWorkingSide / longest)),
                    Math.Max(1, (int)Math.Round((double)page.Height * settings.MaxWorkingSide / longest)));
            }

            var text = await pageReader.ReadPageAsync(scaled ?? page, token);
            var today = (timeProvider ?? TimeProvider.System).TodayInRomeAsDateOnly();
            var result = ParseOcrText(text ?? string.Empty, today);
            logger.LogInformation(
                "Document OCR: fieldsRead={Fields} durationMs={DurationMs}",
                result is null ? 0 : CountFields(result), (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (DocumentReaderUnavailableException ex)
        {
            logger.LogWarning("Document OCR not run: the reader is not available ({Reason})", ex.ReasonCode);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Document OCR timed out; the guided manual entry applies");
            return null;
        }
        catch (InvalidDataException)
        {
            logger.LogInformation("Document OCR: the picture could not be decoded");
            return null;
        }
        catch (Exception ex)
        {
            // The type only: a message could carry what was being read.
            logger.LogError("Document OCR failed ({ErrorType}); the guided manual entry applies", ex.GetType().Name);
            return null;
        }
        finally
        {
            ImageBytes.Zero(bytes);
            scaled?.Dispose();
            page?.Dispose();
            colour?.Dispose();
        }
    }

    private static int CountFields(DocumentOcrResult result) =>
        new object?[]
        {
            result.FullName, result.DocumentNumber, result.DateOfBirth, result.Nationality, result.ExpirationDate, result.Sex,
            result.DateOfIssue, result.PlaceOfIssue,
        }.Count(value => value is not null);

    private enum Field
    {
        Surname,
        GivenNames,
        DocumentNumber,
        DateOfBirth,
        ExpirationDate,
        DateOfIssue,
        PlaceOfIssue,
        Nationality,
        Sex,
    }

    /// <summary>The labels of every field, upper case without accents. A label is matched as a whole word or phrase.</summary>
    private static readonly (Field Field, string[] Labels)[] LabelSets =
    [
        (Field.Surname, ["COGNOME", "SURNAME", "FAMILY NAME", "NOM", "APELLIDOS", "NACHNAME"]),
        (Field.GivenNames, ["NOME", "GIVEN NAMES", "GIVEN NAME", "FIRST NAME", "PRENOMS", "PRENOM", "NOMBRE", "VORNAME", "VORNAMEN"]),
        (Field.DocumentNumber, ["NUMERO DOCUMENTO", "N DOCUMENTO", "DOCUMENT NUMBER", "DOCUMENT NO", "PASSPORT NO", "PASSPORT NUMBER", "CARD NO", "NUMERO DU DOCUMENT", "DOKUMENTENNUMMER", "NUMERO DE DOCUMENTO"]),
        (Field.DateOfBirth, ["DATA DI NASCITA", "NATO IL", "NATA IL", "DATE OF BIRTH", "DATE DE NAISSANCE", "GEBURTSDATUM", "FECHA DE NACIMIENTO"]),
        (Field.ExpirationDate, ["DATA DI SCADENZA", "SCADENZA", "VALIDO FINO AL", "DATE OF EXPIRY", "EXPIRY DATE", "DATE D EXPIRATION", "GULTIG BIS", "FECHA DE CADUCIDAD"]),
        (Field.DateOfIssue, ["DATA DI RILASCIO", "DATA DI EMISSIONE", "RILASCIATO IL", "DATE OF ISSUE", "DATE DE DELIVRANCE", "AUSSTELLUNGSDATUM", "FECHA DE EXPEDICION"]),
        (Field.PlaceOfIssue, ["LUOGO DI RILASCIO", "RILASCIATO DA", "EMESSO DA", "PLACE OF ISSUE", "ISSUING AUTHORITY", "AUTHORITY", "ISSUED BY", "AUTORITE", "AUTORITA", "BEHORDE"]),
        (Field.Nationality, ["CITTADINANZA", "NATIONALITY", "NATIONALITE", "STAATSANGEHORIGKEIT", "NACIONALIDAD"]),
        (Field.Sex, ["SESSO", "SEX", "SEXE", "GESCHLECHT", "SEXO"]),
    ];

    /// <summary>The same labels as whole-word expressions, built once.</summary>
    private static readonly (Field Field, Regex[] Matchers)[] LabelMatchers = LabelSets
        .Select(set => (set.Field, set.Labels
            .Select(label => new Regex(@"\b" + Regex.Escape(label) + @"\b", RegexOptions.Compiled | RegexOptions.CultureInvariant))
            .ToArray()))
        .ToArray();

    private static readonly Regex AnyLabel = new(
        @"\b(?:" + string.Join("|", LabelSets.SelectMany(set => set.Labels).OrderByDescending(label => label.Length).Select(Regex.Escape)) + @")\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumericDate = new(
        @"\b(?<d>\d{1,2})\s*[./\-]\s*(?<m>\d{1,2})\s*[./\-]\s*(?<y>\d{4})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WordDate = new(
        @"\b(?<d>\d{1,2})\s*(?<m>GEN|JAN|FEB|MAR|APR|MAG|MAY|GIU|JUN|LUG|JUL|AGO|AUG|SET|SEP|OTT|OCT|NOV|DIC|DEC)[A-Z]*(?:\s*/\s*[A-Z]{3})?\s*(?<y>\d{4})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] ItalianMonths = ["GEN", "FEB", "MAR", "APR", "MAG", "GIU", "LUG", "AGO", "SET", "OTT", "NOV", "DIC"];

    private static readonly string[] EnglishMonths = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    /// <summary>The number (1 to 12) of a month written as its first three letters in Italian or English; 0 when it is neither.</summary>
    private static int MonthOf(string abbreviation)
    {
        var italian = Array.IndexOf(ItalianMonths, abbreviation);
        return italian >= 0 ? italian + 1 : Array.IndexOf(EnglishMonths, abbreviation) + 1;
    }

    /// <summary>
    /// Reads the printed fields out of the text of a document page: each value after its own label, on the same line or the next, and only when
    /// its format is plausible. Null when no field could be read. The text is not kept.
    /// </summary>
    internal static DocumentOcrResult? ParseOcrText(string ocrText, DateOnly? today = null)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
            return null;

        var now = today ?? TimeProvider.System.TodayInRomeAsDateOnly();
        var lines = ocrText
            .Split('\n')
            .Select(Normalize)
            .Where(line => line.Length > 0)
            .ToList();

        var values = new Dictionary<Field, string>();
        for (var i = 0; i < lines.Count; i++)
        {
            foreach (var (field, matchers) in LabelMatchers)
            {
                if (values.ContainsKey(field))
                    continue;

                var match = matchers
                    .Select(matcher => matcher.Match(lines[i]))
                    .Where(m => m.Success)
                    .OrderByDescending(m => m.Length)
                    .FirstOrDefault();
                if (match is null)
                    continue;

                var value = ValueAfter(lines[i], match.Index + match.Length);
                if (value.Length == 0 && i + 1 < lines.Count && !AnyLabel.IsMatch(lines[i + 1]))
                    value = lines[i + 1];

                if (value.Length > 0)
                    values[field] = value;
            }
        }

        var result = new DocumentOcrResult { Confidence = FallbackConfidence };
        var surname = values.TryGetValue(Field.Surname, out var rawSurname) ? Name(rawSurname) : null;
        var given = values.TryGetValue(Field.GivenNames, out var rawGiven) ? Name(rawGiven) : null;
        result.FullName = surname is null && given is null ? null : string.Join(' ', new[] { given, surname }.Where(part => part is not null));
        result.DocumentNumber = values.TryGetValue(Field.DocumentNumber, out var rawNumber) ? Number(rawNumber) : null;
        result.DateOfBirth = values.TryGetValue(Field.DateOfBirth, out var rawBirth) ? Date(rawBirth, new DateOnly(1900, 1, 1), now) : null;
        result.ExpirationDate = values.TryGetValue(Field.ExpirationDate, out var rawExpiry) ? Date(rawExpiry, now.AddYears(-30), now.AddYears(30)) : null;
        result.DateOfIssue = values.TryGetValue(Field.DateOfIssue, out var rawIssue) ? Date(rawIssue, new DateOnly(1980, 1, 1), now) : null;
        result.PlaceOfIssue = values.TryGetValue(Field.PlaceOfIssue, out var rawPlace) ? Name(rawPlace) : null;
        result.Nationality = values.TryGetValue(Field.Nationality, out var rawNationality) ? Nationality(rawNationality) : null;
        result.Sex = values.TryGetValue(Field.Sex, out var rawSex) ? Sex(rawSex) : null;
        result.IsExpired = result.ExpirationDate is { } expires ? DateOnly.FromDateTime(expires) < now : null;

        return CountFields(result) == 0 ? null : result;
    }

    /// <summary>Upper case, no accents, one space between words, the quotation marks and dashes of the print made plain.</summary>
    private static string Normalize(string line)
    {
        var decomposed = line.ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(c switch
            {
                '’' or '‘' or '`' or '´' => ' ',
                '‐' or '‑' or '‒' or '–' or '—' => '-',
                ' ' or '\t' or '\r' => ' ',
                _ => c,
            });
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>What follows a label on its line, without the same label in another language and up to the next label.</summary>
    private static string ValueAfter(string line, int start)
    {
        var rest = TrimSeparators(line[start..]);
        while (true)
        {
            var twin = AnyLabel.Match(rest);
            if (twin.Success && twin.Index == 0)
            {
                rest = TrimSeparators(rest[twin.Length..]);
                continue;
            }

            if (twin.Success)
                rest = rest[..twin.Index];

            return TrimSeparators(rest);
        }
    }

    private static string TrimSeparators(string text) => text.Trim(' ', ':', '/', '-', '.', '|', ',', ';');

    private static string? Name(string raw)
    {
        var letters = new string(raw.Where(c => c is >= 'A' and <= 'Z' or ' ' or '-' or '\'').ToArray()).Trim();
        letters = Regex.Replace(letters, @"\s+", " ");
        return letters.Count(c => c is >= 'A' and <= 'Z') >= 2 && letters.Length <= 60 ? letters : null;
    }

    private static string? Number(string raw)
    {
        foreach (var token in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.Trim('.', ',', ':', ';');
            if (candidate.Length is >= 5 and <= 12 && candidate.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9') && candidate.Any(char.IsAsciiDigit))
                return candidate;
        }

        return null;
    }

    private static DateTime? Date(string raw, DateOnly earliest, DateOnly latest)
    {
        int day, month, year;
        var numeric = NumericDate.Match(raw);
        if (numeric.Success)
        {
            day = int.Parse(numeric.Groups["d"].Value, CultureInfo.InvariantCulture);
            month = int.Parse(numeric.Groups["m"].Value, CultureInfo.InvariantCulture);
            year = int.Parse(numeric.Groups["y"].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            var words = WordDate.Match(raw);
            if (!words.Success)
                return null;

            day = int.Parse(words.Groups["d"].Value, CultureInfo.InvariantCulture);
            month = MonthOf(words.Groups["m"].Value);
            year = int.Parse(words.Groups["y"].Value, CultureInfo.InvariantCulture);
        }

        try
        {
            var date = new DateOnly(year, month, day);
            return date < earliest || date > latest ? null : new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The three-letter code of the nationality when the print has one; the Italian words for Italian are ITA. Anything else is not guessed.</summary>
    private static string? Nationality(string raw)
    {
        foreach (var token in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token is "ITALIANA" or "ITALIANO" or "ITALIAN" or "ITALIA")
                return "ITA";

            if (token.Length == 3 && token.All(c => c is >= 'A' and <= 'Z'))
                return token;
        }

        return null;
    }

    private static string? Sex(string raw)
    {
        var first = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is "M" or "F" or "X" ? first : null;
    }
}
