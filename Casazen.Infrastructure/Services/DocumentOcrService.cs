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

    private const int CardWorkingSide = 880;

    /// <summary>
    /// Performs OCR on an identity document image when MRZ extraction fails or is not applicable.
    /// Uses Tesseract (Apache-2.0) for text extraction.
    /// </summary>
    public async Task<DocumentOcrResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
        (await ObserveAsync(imageStream, cancellationToken)).Fields;

    public async Task<DocumentOcrObservation> ObserveAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        var settings = options?.Value ?? new MrzOcrOptions();
        var started = Stopwatch.GetTimestamp();
        byte[]? bytes = null;
        RasterImage? colour = null;
        GrayBitmap? page = null;
        GrayBitmap? scaled = null;
        DocumentOcrResult? result = null;
        var pendingCieDateCheck = false;
        try
        {
            if (pageReader is null)
            {
                logger.LogInformation("Document OCR not run: the document reader is not configured");
                return new(null, "unavailable");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var token = deadline.Token;

            bytes = await ImageBytes.ReadAllAsync(imageStream, settings.MaxImageBytes, token);
            colour = RasterImage.Decode(bytes, settings.MaxImagePixels);
            ImageBytes.Zero(bytes);
            page = colour.ToGray();
            using var card = CropBlueCard(colour, page);
            colour.Dispose();

            var today = (timeProvider ?? TimeProvider.System).TodayInRomeAsDateOnly();
            foreach (var candidate in card is null ? new[] { page } : new[] { card, page })
            {
                var longest = Math.Max(candidate.Width, candidate.Height);
                var limit = candidate == card ? Math.Min(CardWorkingSide, settings.MaxWorkingSide) : settings.MaxWorkingSide;
                if (longest > limit)
                {
                    scaled = candidate.Resized(
                        Math.Max(1, (int)Math.Round((double)candidate.Width * limit / longest)),
                        Math.Max(1, (int)Math.Round((double)candidate.Height * limit / longest)));
                }

                var working = scaled ?? candidate;
                int[] orientations = working.Height > working.Width ? [6, 8, 1, 3] : [1, 3, 6, 8];
                foreach (var orientation in orientations)
                {
                    token.ThrowIfCancellationRequested();
                    using var turned = orientation == 1 ? null : working.Oriented(orientation);
                    var text = await pageReader.ReadPageAsync(turned ?? working, token);
                    var read = ParseOcrText(text ?? string.Empty, today);
                    if (read is null)
                        continue;

                    if (result is null || CountFields(read) > CountFields(result))
                        result = read;
                    if (read is { GivenNames: not null, Surname: not null })
                    {
                        result = read;
                        using var orientedCandidate = orientation == 1 ? null : candidate.Oriented(orientation);
                        var detail = orientedCandidate ?? candidate;
                        if (Math.Max(detail.Width, detail.Height) > settings.MaxWorkingSide)
                            detail = turned ?? working;
                        pendingCieDateCheck = candidate == card && detail.Width > detail.Height
                            && (text?.Contains("EMISSIONE", StringComparison.OrdinalIgnoreCase) ?? false);
                        foreach (var sparse in new[] { true, false })
                        {
                            token.ThrowIfCancellationRequested();
                            var layout = await pageReader.ReadLayoutAsync(detail, sparse, token);
                            if (ParseOcrText(layout ?? string.Empty, today) is { } supplement)
                                MergePrintedFields(read, supplement);
                        }
                        if (pendingCieDateCheck && read.DocumentKind == "IdentityCard")
                        {
                            // A partial digit can still give a plausible date. Compare the two CIE rows against
                            // a second segmentation of their printed values before sending them to the guest.
                            var birth = read.DateOfBirth;
                            var issued = read.DateOfIssue;
                            read.DateOfBirth = null;
                            read.DateOfIssue = null;
                            pendingCieDateCheck = false;
                            read.DateOfBirth = await CheckCieDateAsync(detail, birth, birth: true, token);
                            read.DateOfIssue = await CheckCieDateAsync(detail, issued, birth: false, token);
                        }
                        break;
                    }
                }

                scaled?.Dispose();
                scaled = null;
                if (result is { GivenNames: not null, Surname: not null })
                    break;
            }

            logger.LogInformation(
                "Document OCR: fieldsRead={Fields} durationMs={DurationMs}",
                result is null ? 0 : CountFields(result), (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new(result);
        }
        catch (DocumentReaderUnavailableException ex)
        {
            logger.LogWarning("Document OCR not run: the reader is not available ({Reason})", ex.ReasonCode);
            return new(null, "unavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (pendingCieDateCheck && result is not null)
            {
                result.DateOfBirth = null;
                result.DateOfIssue = null;
            }
            logger.LogWarning("Document OCR timed out with {Fields} fields read", result is null ? 0 : CountFields(result));
            return new(result, "unreadable");
        }
        catch (InvalidDataException)
        {
            logger.LogInformation("Document OCR: the picture could not be decoded");
            return new(null, "unreadable");
        }
        catch (Exception ex)
        {
            // The type only: a message could carry what was being read.
            logger.LogError("Document OCR failed ({ErrorType}); the guided manual entry applies", ex.GetType().Name);
            return new(null, "unavailable");
        }
        finally
        {
            ImageBytes.Zero(bytes);
            scaled?.Dispose();
            page?.Dispose();
            colour?.Dispose();
        }
    }

    /// <summary>
    /// Finds a candidate for the blue face of an electronic identity card photographed against a neutral background.
    /// The full image remains a fallback. A crop is only tried if the colour projections describe a card-sized rectangle.
    /// </summary>
    private static GrayBitmap? CropBlueCard(RasterImage image, GrayBitmap page)
    {
        const int sampleStep = 4;
        var columns = new int[image.Width];
        var rows = new int[image.Height];
        for (var y = 0; y < image.Height; y += sampleStep)
        {
            for (var x = 0; x < image.Width; x += sampleStep)
            {
                var offset = (y * image.Width + x) * 3;
                var blue = image.Rgb[offset + 2];
                if (blue - image.Rgb[offset] < 12 || blue - image.Rgb[offset + 1] < -5)
                    continue;

                columns[x]++;
                rows[y]++;
            }
        }

        static (int Start, int End) Bounds(int[] projection)
        {
            var minimum = Math.Max(3, projection.Max() / 10);
            return (Array.FindIndex(projection, count => count >= minimum),
                Array.FindLastIndex(projection, count => count >= minimum));
        }

        var (left, right) = Bounds(columns);
        var (top, bottom) = Bounds(rows);
        var width = right - left + sampleStep;
        var height = bottom - top + sampleStep;
        var shortSide = Math.Min(width, height);
        var aspect = (double)Math.Max(width, height) / shortSide;
        if (left < 0 || top < 0 || shortSide < 240 || aspect is < 1.3 or > 1.9
            || (long)width * height > (long)image.Width * image.Height * 0.9)
            return null;

        var padding = shortSide / 25;
        return page.Cropped(left - padding, top - padding, width + 2 * padding, height + 2 * padding);
    }

    private async Task<DateTime?> CheckCieDateAsync(GrayBitmap card, DateTime? printedDate, bool birth, CancellationToken token)
    {
        if (printedDate is null || pageReader is null)
            return printedDate;

        var left = (int)(card.Width * (birth ? .30 : .31));
        var top = (int)(card.Height * (birth ? .47 : .641));
        var width = (int)(card.Width * (birth ? .37 : .23));
        var height = (int)(card.Height * (birth ? .074 : .075));
        using var line = card.Cropped(left, top, width, height);
        if (line is null)
            return printedDate;

        var confirmed = false;
        using var enlarged = line.Resized(line.Width * 2, line.Height * 2);
        foreach (var candidate in new[] { line, enlarged })
        {
            token.ThrowIfCancellationRequested();
            var text = await pageReader.ReadLayoutAsync(candidate, sparse: false, token);
            var matches = NumericDate.Matches(text ?? string.Empty);
            if (matches.Count != 1 || matches[0].Groups["d"].Length != 2 || matches[0].Groups["m"].Length != 2)
                continue;
            var date = Date(matches[0].Value, new DateOnly(1900, 1, 1), new DateOnly(2100, 12, 31));
            if (date.HasValue && date != printedDate)
                return null;
            confirmed |= date == printedDate;
        }
        return confirmed ? printedDate : null;
    }

    private static void MergePrintedFields(DocumentOcrResult target, DocumentOcrResult other)
    {
        target.HasDocumentEvidence |= other.HasDocumentEvidence;
        target.DocumentKind ??= other.DocumentKind;
        target.DocumentNumber ??= other.DocumentNumber;
        target.DateOfBirth ??= other.DateOfBirth;
        target.BirthPlace ??= other.BirthPlace;
        target.BirthProvince ??= other.BirthProvince;
        target.ResidenceStreet ??= other.ResidenceStreet;
        target.ResidencePostalCode ??= other.ResidencePostalCode;
        target.ResidenceCityName ??= other.ResidenceCityName;
        target.ResidenceProvince ??= other.ResidenceProvince;
        target.DateOfIssue ??= other.DateOfIssue;
        target.PlaceOfIssue ??= other.PlaceOfIssue;
        target.ExpirationDate ??= other.ExpirationDate;
        target.IsExpired ??= other.IsExpired;
        target.Nationality ??= other.Nationality;
        target.Sex ??= other.Sex;
    }

    private static int CountFields(DocumentOcrResult result) =>
        new object?[]
        {
            result.FullName, result.DocumentNumber, result.DateOfBirth, result.Nationality, result.ExpirationDate, result.Sex,
            result.DateOfIssue, result.PlaceOfIssue, result.ResidenceStreet, result.ResidenceCityName,
        }.Count(value => value is not null);

    private enum Field
    {
        Surname,
        GivenNames,
        DocumentNumber,
        DateOfBirth,
        BirthPlace,
        ResidenceAddress,
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
        (Field.GivenNames, ["NOME / NAME", "NOME/NAME", "NOME", "GIVEN NAMES", "GIVEN NAME", "FIRST NAME", "PRENOMS", "PRENOM", "NOMBRE", "VORNAME", "VORNAMEN"]),
        (Field.DocumentNumber, ["NUMERO DOCUMENTO", "N DOCUMENTO", "DOCUMENT NUMBER", "DOCUMENT NO", "PASSPORT NO", "PASSPORT NUMBER", "CARD NO", "NUMERO DU DOCUMENT", "DOKUMENTENNUMMER", "NUMERO DE DOCUMENTO"]),
        (Field.DateOfBirth, ["DATA DI NASCITA", "NATO IL", "NATA IL", "DATE OF BIRTH", "DATE DE NAISSANCE", "GEBURTSDATUM", "FECHA DE NACIMIENTO"]),
        (Field.BirthPlace, ["LUOGO E DATA DI NASCITA", "PLACE AND DATE OF BIRTH", "LUOGO DI NASCITA", "PLACE OF BIRTH", "LIEU DE NAISSANCE", "GEBURTSORT", "LUGAR DE NACIMIENTO"]),
        (Field.ExpirationDate, ["DATA DI SCADENZA", "SCADENZA", "VALIDO FINO AL", "DATE OF EXPIRY", "EXPIRY DATE", "EXPIRY", "DATE D EXPIRATION", "GULTIG BIS", "FECHA DE CADUCIDAD"]),
        (Field.DateOfIssue, ["DATA DI RILASCIO", "DATA DI EMISSIONE", "RILASCIATO IL", "DATE OF ISSUE", "EMISSIONE", "ISSUING", "DATE DE DELIVRANCE", "AUSSTELLUNGSDATUM", "FECHA DE EXPEDICION"]),
        (Field.PlaceOfIssue, ["LUOGO DI RILASCIO", "MUNICIPALITY", "RILASCIATO DA", "EMESSO DA", "PLACE OF ISSUE", "ISSUING AUTHORITY", "AUTHORITY", "ISSUED BY", "AUTORITE", "AUTORITA", "BEHORDE"]),
        (Field.Nationality, ["CITTADINANZA", "NATIONALITY", "NATIONALITE", "STAATSANGEHORIGKEIT", "NACIONALIDAD"]),
        (Field.ResidenceAddress, ["INDIRIZZO DI RESIDENZA", "RESIDENCE ADDRESS", "RESIDENTIAL ADDRESS", "RESIDENCE", "WOHNANSCHRIFT", "DOMICILIO"]),
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
        @"\b(?<d>\d{1,2})\s*[.,/\-]\s*(?<m>\d{1,2})\s*[.,/\-]\s*(?<y>\d{4})\b",
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
                var nameNoise = field is Field.Surname or Field.GivenNames && value.Count(char.IsLetter) < 2;
                if ((!value.Any(char.IsLetterOrDigit) || nameNoise) && i + 1 < lines.Count && !AnyLabel.IsMatch(lines[i + 1]))
                    value = lines[i + 1];

                if (value.Any(char.IsLetterOrDigit))
                    values[field] = value;
            }
        }

        var result = new DocumentOcrResult { Confidence = FallbackConfidence };
        var surname = values.TryGetValue(Field.Surname, out var rawSurname) ? Name(rawSurname) : null;
        var given = values.TryGetValue(Field.GivenNames, out var rawGiven) ? Name(rawGiven) : null;
        result.GivenNames = given;
        result.Surname = surname;
        result.FullName = surname is null && given is null ? null : string.Join(' ', new[] { given, surname }.Where(part => part is not null));
        result.DocumentNumber = values.TryGetValue(Field.DocumentNumber, out var rawNumber) ? Number(rawNumber) : null;
        result.DateOfBirth = values.TryGetValue(Field.DateOfBirth, out var rawBirth) ? Date(rawBirth, new DateOnly(1900, 1, 1), now) : null;
        result.ExpirationDate = values.TryGetValue(Field.ExpirationDate, out var rawExpiry) ? Date(rawExpiry, now.AddYears(-30), now.AddYears(30)) : null;
        result.DateOfIssue = values.TryGetValue(Field.DateOfIssue, out var rawIssue) ? Date(rawIssue, new DateOnly(1980, 1, 1), now) : null;
        result.PlaceOfIssue = values.TryGetValue(Field.PlaceOfIssue, out var rawPlace) ? Name(rawPlace) : null;
        result.Nationality = values.TryGetValue(Field.Nationality, out var rawNationality) ? Nationality(rawNationality) : null;
        result.Sex = values.TryGetValue(Field.Sex, out var rawSex) ? Sex(rawSex) : null;
        var pageText = string.Join('\n', lines);
        result.DocumentKind = Regex.IsMatch(pageText, @"\b(?:CARTA\s*DI\s*IDENTITA|IDENTITY CARD|CARTE D.IDENTITE|PERSONALAUSWEIS|DOCUMENTO NACIONAL DE IDENTIDAD)\b") ? "IdentityCard"
            : Regex.IsMatch(pageText, @"\b(?:PASSAPORTO|PASSPORT|PASSEPORT|REISEPASS|PASAPORTE)\b") ? "Passport"
            : Regex.IsMatch(pageText, @"\b(?:PATENTE DI GUIDA|DRIV(?:ING|ER.S) LICEN[CS]E|PERMIS DE CONDUIRE|FUHRERSCHEIN)\b") ? "DriversLicense" : null;
        var cieNumber = Regex.Match(pageText, @"\bC[A-Z]\d{5}[A-Z]{2}\b");
        var italianCard = pageText.Contains("COGNOME") && pageText.Contains("NOME") && (pageText.Contains("IDENTITA")
            || cieNumber.Success || (pageText.Contains("EMISSIONE") && pageText.Contains("SCADENZA") && pageText.Contains("HOLDER")));
        if (italianCard)
        {
            result.DocumentKind ??= "IdentityCard";
            if (cieNumber.Success)
                result.DocumentNumber ??= cieNumber.Value;
        }
        if (values.TryGetValue(Field.BirthPlace, out var birthLine))
        {
            var place = Regex.Match(birthLine, @"(?<place>[A-Z][A-Z' -]+?)\s*[({]\s*(?<province>[A-Z]{2})\s*[)}]");
            if (place.Success && Casazen.Core.Regulatory.AlloggiatiRecordRules.IsValidProvince(place.Groups["province"].Value))
            {
                result.BirthPlace = Name(place.Groups["place"].Value);
                result.BirthProvince = place.Groups["province"].Value;
            }
            else if (!birthLine.Any(char.IsDigit))
                result.BirthPlace = Name(birthLine);
        }
        if (italianCard)
        {
            // The CIE prints town, province and birth date on one line and issue/expiry dates on another.
            var nameRow = lines.FindIndex(line => Regex.IsMatch(line, @"\b(?:NOME|GIVEN NAMES?)\b"));
            var lastBirthRow = nameRow < 0 ? -1 : lines.FindIndex(nameRow + 1, line =>
                Regex.IsMatch(line, @"\b(?:SESSO|SEX|EMISSIONE|SCADENZA|RESIDENCE)\b"));
            if (lastBirthRow < 0)
                lastBirthRow = Math.Min(lines.Count, nameRow + 8);
            for (int lineIndex = nameRow + 1; nameRow >= 0 && lineIndex < lastBirthRow; lineIndex++)
            {
                var line = lines[lineIndex];
                var combinedBirth = Regex.Match(line, @"(?<place>[A-Z][A-Z' -]+?)\s*[(]\s*(?<province>[A-Z]{2})\s*[)}]\s*(?<date>\d.*)");
                if (combinedBirth.Success && Casazen.Core.Regulatory.AlloggiatiRecordRules.IsValidProvince(combinedBirth.Groups["province"].Value))
                {
                    result.BirthPlace ??= Name(combinedBirth.Groups["place"].Value);
                    result.BirthProvince ??= combinedBirth.Groups["province"].Value;
                    result.DateOfBirth ??= Date(combinedBirth.Groups["date"].Value, new DateOnly(1900, 1, 1), now);
                }
            }
            var sexRow = lines.FindIndex(line => Regex.IsMatch(line, @"\b(?:SESSO|SEX)\b"));
            var issueRow = lines.FindIndex(line => line.Contains("EMISSIONE"));
            if (sexRow >= 0 && issueRow > sexRow)
            {
                var details = string.Join(' ', lines.GetRange(sexRow, issueRow - sexRow));
                var row = Regex.Match(details, @"\b(?<sex>M{1,2}|F{1,2}|X{1,2})\s+\d{3}\s+(?<country>[A-Z]{3})\b");
                if (row.Success)
                {
                    result.Sex ??= row.Groups["sex"].Value[..1];
                    result.Nationality ??= row.Groups["country"].Value;
                }
            }
            for (int i = 0; i + 1 < lines.Count; i++)
            {
                if (!lines[i].Contains("EMISSIONE"))
                    continue;
                var sameRow = lines[i].Contains("SCADENZA");
                var adjacentLabels = !sameRow && lines[i + 1].Contains("SCADENZA") && !NumericDate.IsMatch(lines[i + 1]);
                if (!sameRow && !adjacentLabels)
                    continue;
                result.DateOfIssue = null;
                result.ExpirationDate = null;
                if (adjacentLabels)
                    continue;
                var dates = NumericDate.Matches(lines[i + 1]);
                if (dates.Count != 2 || dates.Any(date => date.Groups["d"].Length != 2 || date.Groups["m"].Length != 2))
                    continue;
                var issued = Date(dates[0].Value, new DateOnly(1980, 1, 1), now);
                var expiryDate = Date(dates[1].Value, now.AddYears(-30), now.AddYears(30));
                if (issued < expiryDate)
                {
                    result.DateOfIssue = issued;
                    result.ExpirationDate = expiryDate;
                }
            }
        }
        if (values.TryGetValue(Field.ResidenceAddress, out var address))
        {
            var addressLine = lines.IndexOf(address);
            if (addressLine >= 0 && addressLine + 1 < lines.Count && !AnyLabel.IsMatch(lines[addressLine + 1]))
                ReadResidence(result, address + " " + lines[addressLine + 1]);
            if (result.ResidenceStreet is null)
                ReadResidence(result, address);
        }
        result.HasDocumentEvidence = (result.DocumentKind is not null && (result.DateOfBirth is not null || result.DocumentNumber is not null
            || result.ExpirationDate is not null || result.DateOfIssue is not null || result.PlaceOfIssue is not null || result.Nationality is not null)) ||
            (result.DocumentNumber is not null && (result.DateOfBirth is not null || result.ExpirationDate is not null || result.Nationality is not null));
        result.IsExpired = result.ExpirationDate is { } expires ? DateOnly.FromDateTime(expires) < now : null;

        return CountFields(result) == 0 ? null : result;
    }

    private static void ReadResidence(DocumentOcrResult result, string address)
    {
        // Only the address printed after the residence label. A town elsewhere may be the birthplace or issuing authority.
        var match = Regex.Match(address, @"^(?<street>(?:VIA(?:LE)?|PIAZZA(?:LE)?|CORSO|VICOLO|LARGO|STRADA|LOCALITA) [A-Z0-9'., /-]{1,110}?\s+\d+(?:/\w+)?)\s*[,;-]?\s*(?:(?<postal>\d{5})\s+)?(?<city>[A-Z][A-Z' -]{1,65})\s*\((?<province>[A-Z]{2})\)\s*$");
        if (!match.Success || !Casazen.Core.Regulatory.AlloggiatiRecordRules.IsValidProvince(match.Groups["province"].Value))
            return;
        result.ResidenceStreet = match.Groups["street"].Value;
        result.ResidenceCityName = match.Groups["city"].Value.Trim();
        result.ResidencePostalCode = match.Groups["postal"].Success ? match.Groups["postal"].Value : null;
        result.ResidenceProvince = match.Groups["province"].Value;
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
