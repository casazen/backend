using System.Diagnostics;
using System.Globalization;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// MRZ extraction service supporting passport (TD-3), ID card (TD-1), and residence permit (TD-2)
/// per ICAO 9303 (ADR-009-R22, ADR-013-R10). Validates check digits per ADR-009-R23, the composite one included.
/// <para>
/// The picture is read from memory only (ADR-013-R6): decoded, levelled, the lines of the MRZ cut out (<see cref="MrzImageLocator"/>) and read
/// with the OCR-B model of an in-process Tesseract (<see cref="TesseractMrzTextReader"/>); the pixels are zeroed before returning. <b>No MRZ
/// text, name, number or date is ever logged</b>: the log has counts and codes. A reading with a failed check digit is never presented as
/// certain: <c>CheckDigitsValid</c> is false, the confidence low, and a field whose own digit failed is not returned. A document with nothing
/// trustworthy gives null and the guided manual entry applies.
/// </para>
/// </summary>
public class MrzExtractionService(
    ILogger<MrzExtractionService> logger,
    IMrzBandLocator? locator = null,
    IMrzTextReader? textReader = null,
    IOptions<MrzOcrOptions>? options = null,
    TimeProvider? timeProvider = null) : IMrzExtractionService
{
    // ICAO 9303 check digit weights for mod-10 weighted sum
    private static readonly int[] CheckDigitWeights = [7, 3, 1];

    /// <summary>Confidence of a reading whose check digits all pass.</summary>
    internal const double CertainConfidence = 0.95;

    /// <summary>Confidence of a reading with a failed check digit.</summary>
    internal const double DoubtfulConfidence = 0.40;

    /// <summary>The turns tried, as EXIF orientation values (1 as it is, 6 a quarter clockwise, 8 a quarter counter-clockwise, 3 a half).</summary>
    private static readonly int[] TurnsLandscape = [1, 6, 8, 3];

    private static readonly int[] TurnsPortrait = [6, 8, 1, 3];

    /// <summary>
    /// Extracts data from the MRZ of an identity document image. Validates check digits per ICAO 9303.
    /// For passports (TD-3): 2 lines of 44 characters each.
    /// For ID cards (TD-1): 3 lines of 30 characters each.
    /// For residence permits (TD-2): 2 lines of 36 characters each.
    /// </summary>
    public async Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
        (await ReadAsync(imageStream, cancellationToken)).Result;

    /// <summary>The reading and the quality hints of the picture, also when nothing could be read.</summary>
    public async Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        var settings = options?.Value ?? new MrzOcrOptions();
        var started = Stopwatch.GetTimestamp();
        byte[]? bytes = null;
        RasterImage? colour = null;
        GrayBitmap? page = null;
        GrayBitmap? turned = null;
        try
        {
            if (locator is null || textReader is null)
            {
                logger.LogInformation("MRZ reading not run: the document reader is not configured");
                return new MrzReading(null, []);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var token = deadline.Token;

            bytes = await ImageBytes.ReadAllAsync(imageStream, settings.MaxImageBytes, token);
            colour = RasterImage.Decode(bytes, settings.MaxImagePixels);
            ImageBytes.Zero(bytes);
            page = colour.ToGray();
            colour.Dispose();

            var today = (timeProvider ?? TimeProvider.System).TodayInRomeAsDateOnly();
            Attempt? best = null;
            foreach (var turn in page.Height > page.Width * 1.15 ? TurnsPortrait : TurnsLandscape)
            {
                turned = turn == 1 ? null : page.Oriented(turn);
                Attempt? attempt;
                using (var location = locator.Locate(turned ?? page, token))
                    attempt = await ReadGroupsAsync(location, today, settings, token);

                turned?.Dispose();
                turned = null;
                if (attempt is null)
                    continue;

                if (attempt.Result.CheckDigitsValid)
                {
                    logger.LogInformation(
                        "MRZ reading: format={Format} checkDigitsValid=true durationMs={DurationMs}",
                        attempt.Format, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return new MrzReading(attempt.Result, []);
                }

                if (best is null || attempt.Rank > best.Rank)
                    best = attempt;
            }

            if (best is not null)
            {
                best.Result.Quality = best.Hints;
                logger.LogInformation(
                    "MRZ reading: format={Format} checkDigitsValid=false hints={Hints} durationMs={DurationMs}",
                    best.Format, best.Hints.Count, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return new MrzReading(best.Result, best.Hints);
            }

            logger.LogInformation("MRZ reading: nothing readable durationMs={DurationMs}", (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new MrzReading(null, [MrzQualityCodes.MrzNotFound]);
        }
        catch (DocumentReaderUnavailableException ex)
        {
            logger.LogWarning("MRZ reading not run: the OCR reader is not available ({Reason})", ex.ReasonCode);
            return new MrzReading(null, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("MRZ reading timed out; the guided manual entry applies");
            return new MrzReading(null, []);
        }
        catch (InvalidDataException)
        {
            logger.LogInformation("MRZ reading: the picture could not be decoded");
            return new MrzReading(null, [MrzQualityCodes.MrzNotFound]);
        }
        catch (Exception ex)
        {
            // The type only: a message could carry what was being read.
            logger.LogError("MRZ reading failed ({ErrorType}); the guided manual entry applies", ex.GetType().Name);
            return new MrzReading(null, []);
        }
        finally
        {
            ImageBytes.Zero(bytes);
            turned?.Dispose();
            page?.Dispose();
            colour?.Dispose();
        }
    }

    /// <summary>What reading one picture orientation found: the best interpretation, which format it is and what the picture raises.</summary>
    private sealed record Attempt(MrzExtractionResult Result, string Format, IReadOnlyList<string> Hints)
    {
        /// <summary>Valid digits first, then the fields whose own digit passed, then the confidence.</summary>
        public double Rank =>
            (Result.CheckDigitsValid ? 100 : 0)
            + (Result.DocumentNumber is null ? 0 : 10)
            + (Result.DateOfBirth is null ? 0 : 10)
            + (Result.ExpirationDate is null ? 0 : 10)
            + Result.Confidence;
    }

    /// <summary>Reads the candidate blocks of a location in order, the gray strips first and then the binarised ones; the first valid reading wins.</summary>
    private async Task<Attempt?> ReadGroupsAsync(MrzLocation location, DateOnly today, MrzOcrOptions settings, CancellationToken cancellationToken)
    {
        if (location.Groups.Count == 0)
            return null;

        var grayText = new string?[location.Lines.Count];
        var binaryText = new string?[location.Lines.Count];
        var grayRead = new bool[location.Lines.Count];
        var binaryRead = new bool[location.Lines.Count];
        Attempt? best = null;
        foreach (var group in location.Groups)
        {
            foreach (var useBinary in new[] { false, true })
            {
                var texts = useBinary ? binaryText : grayText;
                var done = useBinary ? binaryRead : grayRead;
                var cleaned = new List<string>();
                foreach (var index in group)
                {
                    if (!done[index])
                    {
                        var strip = useBinary ? location.Lines[index].Binary : location.Lines[index].Gray;
                        texts[index] = await textReader!.ReadLineAsync(strip, cancellationToken);
                        done[index] = true;
                    }

                    cleaned.Add(CleanLine(texts[index]));
                }

                var reading = Interpret(cleaned, today);
                if (reading is null)
                    continue;

                IReadOnlyList<string> hints = reading.Value.Result.CheckDigitsValid ? [] : QualityHints(location, group, settings);
                var attempt = new Attempt(reading.Value.Result, reading.Value.Format, hints);
                if (attempt.Result.CheckDigitsValid)
                    return attempt;

                if (best is null || attempt.Rank > best.Rank)
                    best = attempt;
            }
        }

        // A block that was found but could not be read: the hints of the best block say why (a reflection, a shaken hand).
        return best;
    }

    private static IReadOnlyList<string> QualityHints(MrzLocation location, IReadOnlyList<int> group, MrzOcrOptions settings)
    {
        var hints = new List<string>();
        if (group.Average(i => location.Lines[i].GlareShare) > settings.GlareShareThreshold)
            hints.Add(MrzQualityCodes.Glare);

        if (group.Average(i => location.Lines[i].BlurRatio) > settings.BlurRatioThreshold)
            hints.Add(MrzQualityCodes.Blur);

        if (hints.Count == 0)
            hints.Add(MrzQualityCodes.MrzNotFound);

        return hints;
    }

    /// <summary>The characters of one OCR-B line: upper case, no spaces, nothing outside A-Z, 0-9 and the filler; the longest part when the OCR returned several lines.</summary>
    internal static string CleanLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var best = string.Empty;
        foreach (var part in raw.Split('\n'))
        {
            var chars = new List<char>(part.Length);
            foreach (var c in part.ToUpperInvariant())
            {
                if (c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '<')
                    chars.Add(c);
                else if (c is '«' or '‹' or '〈' or '〈' or '＜' or '(' or '[' or '{')
                    chars.Add('<');
            }

            if (chars.Count > best.Length)
                best = new string(chars.ToArray());
        }

        return best;
    }

    /// <summary>Which format the cleaned lines of a block are, and what they say; null when they are no MRZ at all.</summary>
    internal static (MrzExtractionResult Result, string Format)? Interpret(IReadOnlyList<string> lines, DateOnly today)
    {
        var candidates = new List<(MrzExtractionResult Result, string Format)>();
        if (lines.Count == 3)
        {
            if (FitAll(lines, 30) is { } cardLines && ParseTd1Mrz(cardLines[0], cardLines[1], cardLines[2], today) is { } card)
                candidates.Add((card, "TD1"));

            // A block of three lines can be an MRZ of two with one line of other text above or below it.
            AddTwoLines(candidates, lines[1], lines[2], today);
            AddTwoLines(candidates, lines[0], lines[1], today);
        }
        else if (lines.Count == 2)
        {
            AddTwoLines(candidates, lines[0], lines[1], today);
        }

        return candidates.Count == 0
            ? null
            : candidates
                .OrderByDescending(c => c.Result.CheckDigitsValid)
                .ThenByDescending(c => (c.Result.DocumentNumber is null ? 0 : 1) + (c.Result.DateOfBirth is null ? 0 : 1) + (c.Result.ExpirationDate is null ? 0 : 1))
                .First();
    }

    private static void AddTwoLines(List<(MrzExtractionResult Result, string Format)> candidates, string first, string second, DateOnly today)
    {
        if (FitAll([first, second], 44) is { } passportLines && ParseTd3Mrz(passportLines[0], passportLines[1], today) is { } passport)
            candidates.Add((passport, "TD3"));
        else if (FitAll([first, second], 36) is { } permitLines && ParseTd2Mrz(permitLines[0], permitLines[1], today) is { } permit)
            candidates.Add((permit, "TD2"));
    }

    /// <summary>The lines padded with the filler or cut to <paramref name="length"/> when each is within three characters of it; null otherwise.</summary>
    private static string[]? FitAll(IReadOnlyList<string> lines, int length)
    {
        var fitted = new string[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (Math.Abs(line.Length - length) > 3)
                return null;

            fitted[i] = line.Length == length
                ? line
                : line.Length < length ? line.PadRight(length, '<') : line[..length];
        }

        return fitted;
    }

    /// <summary>
    /// Validates a check digit per ICAO 9303 mod-10 weighted sum algorithm (ADR-009-R23). The check character must be a digit, except that
    /// the filler is accepted for a field that is nothing but fillers.
    /// </summary>
    /// <param name="data">The data string (document number, date, etc.).</param>
    /// <param name="checkDigit">The check digit to validate.</param>
    /// <returns>True if the check digit is valid.</returns>
    internal static bool ValidateCheckDigit(string data, char checkDigit)
    {
        if (string.IsNullOrEmpty(data))
            return false;

        if (checkDigit == '<')
            return data.All(c => c == '<');

        if (checkDigit is < '0' or > '9')
            return false;

        var sum = 0;
        for (var i = 0; i < data.Length; i++)
        {
            var value = CharToValue(data[i]);
            var weight = CheckDigitWeights[i % CheckDigitWeights.Length];
            sum += value * weight;
        }

        var calculatedCheckDigit = sum % 10;
        var providedCheckDigit = CharToValue(checkDigit);

        return calculatedCheckDigit == providedCheckDigit;
    }

    /// <summary>
    /// Converts a character to its ICAO 9303 numeric value.
    /// Digits 0-9 = 0-9, Letters A-Z = 10-35, filler &lt; = 0.
    /// </summary>
    private static int CharToValue(char c)
    {
        return c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'Z' => c - 'A' + 10,
            '<' => 0,
            _ => 0
        };
    }

    /// <summary>
    /// Parses a date from MRZ format (YYMMDD) to DateTime. The century is not in the MRZ: a date of birth is the latest year of that ending
    /// that is not in the future; an expiry date is the year of that ending from 2000 to 30 years ahead, and 19xx past it (a document that
    /// expired long ago). Null when the six characters are not a real date.
    /// </summary>
    /// <param name="yymmdd">Six digits.</param>
    /// <param name="isExpiry">True for a date of expiry, false for a date of birth.</param>
    /// <param name="today">Today in Europe/Rome; read from the system clock when not given.</param>
    internal static DateTime? ParseMrzDate(string yymmdd, bool isExpiry = false, DateOnly? today = null)
    {
        if (string.IsNullOrWhiteSpace(yymmdd) || yymmdd.Length != 6 || !yymmdd.All(char.IsAsciiDigit))
            return null;

        var yy = int.Parse(yymmdd[..2], CultureInfo.InvariantCulture);
        var mm = int.Parse(yymmdd[2..4], CultureInfo.InvariantCulture);
        var dd = int.Parse(yymmdd[4..6], CultureInfo.InvariantCulture);
        var now = today ?? TimeProvider.System.TodayInRomeAsDateOnly();

        try
        {
            if (isExpiry)
                return new DateTime(yy <= (now.Year % 100) + 30 ? 2000 + yy : 1900 + yy, mm, dd, 0, 0, 0, DateTimeKind.Utc);

            var born = new DateTime(2000 + yy, mm, dd, 0, 0, 0, DateTimeKind.Utc);
            return DateOnly.FromDateTime(born) > now ? new DateTime(1900 + yy, mm, dd, 0, 0, 0, DateTimeKind.Utc) : born;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses TD-3 (passport) MRZ format: 2 lines of 44 characters.
    /// Line 1: P&lt;ITA&lt;&lt;SURNAME&lt;&lt;GIVENNAMES (type, issuing state, name)
    /// Line 2: document number, nationality, DOB, sex, expiration, personal number (each with its check digit) and the composite digit.
    /// </summary>
    internal static MrzExtractionResult? ParseTd3Mrz(string line1, string line2, DateOnly? today = null)
    {
        if (line1.Length != 44 || line2.Length != 44)
            return null;

        var corrected = false;
        var document = line2[..9];
        var documentCheck = Numeric(line2[9..10], ref corrected)[0];
        var birth = Numeric(line2[13..19], ref corrected);
        var birthCheck = Numeric(line2[19..20], ref corrected)[0];
        var expiry = Numeric(line2[21..27], ref corrected);
        var expiryCheck = Numeric(line2[27..28], ref corrected)[0];
        var personal = line2[28..42];
        var personalCheck = Numeric(line2[42..43], ref corrected)[0];
        var compositeCheck = Numeric(line2[43..44], ref corrected)[0];
        var personalValid = personal.All(c => c == '<')
            ? (personalCheck == '<' || personalCheck == '0')
            : ValidateCheckDigit(personal, personalCheck);
        var composite = document + documentCheck + birth + birthCheck + expiry + expiryCheck + personal + personalCheck;

        return Build(
            "Passport", line1[..2], Letters(line1[2..5], ref corrected), line1[5..], Letters(line2[10..13], ref corrected), line2[20],
            document, documentCheck, birth, birthCheck, expiry, expiryCheck, personalValid, composite, compositeCheck, corrected,
            today ?? TimeProvider.System.TodayInRomeAsDateOnly());
    }

    /// <summary>
    /// Parses TD-2 (residence permit and some ID cards) MRZ format: 2 lines of 36 characters.
    /// Line 1: document code, issuing state, name. Line 2: document number, nationality, DOB, sex, expiration, optional data and the composite digit.
    /// A document number longer than nine characters continues in the optional data (ICAO 9303 part 6).
    /// </summary>
    internal static MrzExtractionResult? ParseTd2Mrz(string line1, string line2, DateOnly? today = null)
    {
        if (line1.Length != 36 || line2.Length != 36)
            return null;

        var corrected = false;
        var document = line2[..9];
        var documentCheck = Numeric(line2[9..10], ref corrected)[0];
        var birth = Numeric(line2[13..19], ref corrected);
        var birthCheck = Numeric(line2[19..20], ref corrected)[0];
        var expiry = Numeric(line2[21..27], ref corrected);
        var expiryCheck = Numeric(line2[27..28], ref corrected)[0];
        var optional = line2[28..35];
        var compositeCheck = Numeric(line2[35..36], ref corrected)[0];

        // The composite digit covers the fields as they are written, before a long document number is joined.
        var composite = document + documentCheck + birth + birthCheck + expiry + expiryCheck + optional;
        ResolveLongDocumentNumber(ref document, ref documentCheck, optional);

        return Build(
            "ResidencePermit", line1[..2], Letters(line1[2..5], ref corrected), line1[5..], Letters(line2[10..13], ref corrected), line2[20],
            document, documentCheck, birth, birthCheck, expiry, expiryCheck, true, composite, compositeCheck, corrected,
            today ?? TimeProvider.System.TodayInRomeAsDateOnly());
    }

    /// <summary>
    /// Parses TD-1 (ID card) MRZ format: 3 lines of 30 characters. Line 1: document code, issuing state, document number with its check digit,
    /// optional data. Line 2: DOB, sex, expiration, nationality, optional data, composite digit. Line 3: the name.
    /// </summary>
    internal static MrzExtractionResult? ParseTd1Mrz(string line1, string line2, string line3, DateOnly? today = null)
    {
        if (line1.Length != 30 || line2.Length != 30 || line3.Length != 30)
            return null;

        var corrected = false;
        var document = line1[5..14];
        var documentCheck = Numeric(line1[14..15], ref corrected)[0];
        var optionalFirst = line1[15..30];
        var birth = Numeric(line2[..6], ref corrected);
        var birthCheck = Numeric(line2[6..7], ref corrected)[0];
        var expiry = Numeric(line2[8..14], ref corrected);
        var expiryCheck = Numeric(line2[14..15], ref corrected)[0];
        var optionalSecond = line2[18..29];
        var compositeCheck = Numeric(line2[29..30], ref corrected)[0];

        // The composite digit covers the fields as they are written, before a long document number is joined.
        var composite = document + documentCheck + optionalFirst + birth + birthCheck + expiry + expiryCheck + optionalSecond;
        ResolveLongDocumentNumber(ref document, ref documentCheck, optionalFirst);

        return Build(
            "IdCard", line1[..2], Letters(line1[2..5], ref corrected), line3, Letters(line2[15..18], ref corrected), line2[7],
            document, documentCheck, birth, birthCheck, expiry, expiryCheck, true, composite, compositeCheck, corrected,
            today ?? TimeProvider.System.TodayInRomeAsDateOnly());
    }

    /// <summary>
    /// A document number longer than nine characters is written in the first nine, a filler in the place of its check digit, and the rest
    /// followed by the check digit in the optional data: join them. Nothing changes for a number that fits.
    /// </summary>
    private static void ResolveLongDocumentNumber(ref string document, ref char documentCheck, string optional)
    {
        if (documentCheck != '<')
            return;

        var end = optional.IndexOf('<');
        if (end < 2)
            return;

        document += optional[..(end - 1)];
        documentCheck = optional[end - 1];
    }

    /// <summary>Builds the result of any format from the fields and the outcome of every digit; null when no field of the document has a valid digit.</summary>
    private static MrzExtractionResult? Build(
        string formatDocumentType,
        string documentCode,
        string issuingState,
        string nameField,
        string nationality,
        char sex,
        string document,
        char documentCheck,
        string birth,
        char birthCheck,
        string expiry,
        char expiryCheck,
        bool otherDigitsValid,
        string composite,
        char compositeCheck,
        bool corrected,
        DateOnly today)
    {
        var documentValid = ValidateCheckDigit(document, documentCheck);
        var birthValid = ValidateCheckDigit(birth, birthCheck);
        var expiryValid = ValidateCheckDigit(expiry, expiryCheck);
        if (!documentValid && !birthValid && !expiryValid)
            return null;

        var compositeValid = ValidateCheckDigit(composite, compositeCheck);
        var valid = documentValid && birthValid && expiryValid && otherDigitsValid && compositeValid;
        var (surname, givenNames) = ParseNames(nameField);
        var result = new MrzExtractionResult
        {
            DocumentType = DocumentTypeOf(documentCode, formatDocumentType),
            IssuingState = issuingState.Replace("<", string.Empty).Trim(),
            Surname = surname,
            GivenNames = givenNames,
            Nationality = nationality.Replace("<", string.Empty).Trim(),
            DocumentNumber = documentValid ? document.Replace("<", string.Empty).Trim() : null,
            DateOfBirth = birthValid ? ParseMrzDate(birth, isExpiry: false, today) : null,
            ExpirationDate = expiryValid ? ParseMrzDate(expiry, isExpiry: true, today) : null,
            Sex = sex is 'M' or 'F' or 'X' ? sex.ToString() : null,
            CheckDigitsValid = valid,
            Confidence = valid ? (corrected ? CertainConfidence - 0.05 : CertainConfidence) : DoubtfulConfidence,
        };
        result.IsExpired = result.ExpirationDate is { } expires ? DateOnly.FromDateTime(expires) < today : null;
        return result;
    }

    /// <summary>Surname before the double filler, given names after it; single fillers are spaces; digits the OCR took for letters are corrected.</summary>
    private static (string? Surname, string? GivenNames) ParseNames(string nameField)
    {
        var corrected = false;
        var field = Letters(nameField, ref corrected).TrimEnd('<');
        var split = field.IndexOf("<<", StringComparison.Ordinal);
        var surname = Words(split < 0 ? field : field[..split]);
        var given = split < 0 ? string.Empty : Words(field[(split + 2)..]);
        return (surname.Length == 0 ? null : surname, given.Length == 0 ? null : given);
    }

    private static string Words(string text) => string.Join(' ', text.Replace('<', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string DocumentTypeOf(string code, string byFormat) =>
        code.Length < 2
            ? byFormat
            : code[0] switch
            {
                'P' => "Passport",
                'A' => "ResidencePermit",
                'I' => code[1] == 'R' ? "ResidencePermit" : "IdCard",
                _ => byFormat,
            };

    /// <summary>A numeric field read by an OCR-B model: the letters it confuses with digits go back to digits (O, I, S, B, Z for 0, 1, 5, 8, 2).</summary>
    internal static string Numeric(string value, ref bool corrected)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var fixedChar = chars[i] switch
            {
                'O' => '0',
                'I' => '1',
                'S' => '5',
                'B' => '8',
                'Z' => '2',
                var other => other,
            };
            if (fixedChar != chars[i])
            {
                chars[i] = fixedChar;
                corrected = true;
            }
        }

        return new string(chars);
    }

    /// <summary>An alphabetic field (a country code, a name): the digits an OCR-B model confuses with letters go back to letters (0, 1, 5, 8, 2 for O, I, S, B, Z).</summary>
    internal static string Letters(string value, ref bool corrected)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var fixedChar = chars[i] switch
            {
                '0' => 'O',
                '1' => 'I',
                '5' => 'S',
                '8' => 'B',
                '2' => 'Z',
                var other => other,
            };
            if (fixedChar != chars[i])
            {
                chars[i] = fixedChar;
                corrected = true;
            }
        }

        return new string(chars);
    }
}
