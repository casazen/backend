using System.Globalization;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Core.Regulatory;

/// <summary>
/// The text file ("tracciato record") the host uploads on the Alloggiati Web portal, menu "File" (CO-13). One line of
/// 168 characters per guest of the stay, a head of family or group followed by its members, lines separated by CR+LF with
/// none after the last. Building the file is not a communication: nothing is transmitted and no status changes (D6).
/// </summary>
/// <remarks>
/// Sources, both official documents of the Polizia di Stato (Centro Elettronico Nazionale), read in full on 2026-10-01
/// (see <c>docs/runbooks/alloggiati.md</c>, "Record file (CO-13)", for where the copies come from and their checksums):
/// <c>MANUALEALBERGHI.pdf</c> ("Guida servizio Alloggiati Web", paragraph 12 "File tracciato record" and the tables at pages
/// 34-35) and <c>MANUALEWS.pdf</c> ("WS_ALLOGGIATI Documento di Descrizione" Rev. 01 of 24/01/2022, chapter 4 "Tracciati
/// record", pages 19-20). Both give the same table, reproduced by <see cref="Layout"/>.
/// <list type="bullet">
/// <item>Encoding UTF-8, at most <see cref="MaxLines"/> lines. Everything CasaZen writes is plain ASCII (codes of the official
/// tables, names reduced to the letters A-Z, see <see cref="ToRecordName"/>), so UTF-8 and the character count agree.</item>
/// <item>Fields of fixed length padded with spaces on the right. Dates are <c>dd/MM/yyyy</c>; the days of stay are written with
/// two digits (<c>03</c>), the manuals only say "2 caratteri".</item>
/// <item>Comune and province of birth are written only for guests born in Italy (9 and 2 spaces otherwise); the state of birth
/// is always written, with the code of Italy for those born in Italy.</item>
/// <item>Only a single guest, a head of family or a head of group carry the document: for the other guests the three document
/// fields (34 characters) are spaces.</item>
/// <item>Codes (kind of guest, comune, state, document type) come only from the official tables imported by an admin (CO-12):
/// without them the file is not built.</item>
/// </list>
/// The "File Unico" of the profile "Gestione Appartamenti" (174 characters, id of the apartment at positions 168-173) is not
/// built: the manuals do not say how the id is aligned and padded, and the portal lets that profile pick the apartment when
/// the plain 168-character file is uploaded (<c>MANUALEALBERGHI.pdf</c>, paragraph 8).
/// </remarks>
public static class AlloggiatiRecordFile
{
    /// <summary>Characters of a line, without the line break.</summary>
    public const int LineLength = 168;

    /// <summary>Lines of one file: one per guest, at most 1000 (<c>MANUALEALBERGHI.pdf</c>, paragraph 12).</summary>
    public const int MaxLines = 1000;

    /// <summary>Between two lines: ASCII 13 and 10; not after the last line.</summary>
    public const string LineBreak = "\r\n";

    /// <summary>Format of the arrival and birth dates.</summary>
    public const string DateFormat = "dd/MM/yyyy";

    /// <summary>One field of the line: <see cref="Start"/> is the 0-based position ("DA"), <see cref="Length"/> the characters.</summary>
    public readonly record struct FieldSpan(string Name, int Start, int Length)
    {
        /// <summary>Position of the last character ("A" of the official table).</summary>
        public int End => Start + Length - 1;
    }

    /// <summary>The official table of the record (<c>MANUALEALBERGHI.pdf</c> p. 34, <c>MANUALEWS.pdf</c> p. 19), in order.</summary>
    public static IReadOnlyList<FieldSpan> Layout { get; } =
    [
        new("TipoAlloggiato", 0, 2),
        new("DataArrivo", 2, 10),
        new("GiorniPermanenza", 12, 2),
        new("Cognome", 14, 50),
        new("Nome", 64, 30),
        new("Sesso", 94, 1),
        new("DataNascita", 95, 10),
        new("ComuneNascita", 105, 9),
        new("ProvinciaNascita", 114, 2),
        new("StatoNascita", 116, 9),
        new("Cittadinanza", 125, 9),
        new("TipoDocumento", 134, 5),
        new("NumeroDocumento", 139, 20),
        new("LuogoRilascioDocumento", 159, 9),
    ];

    private static readonly Dictionary<char, string> Letters = new()
    {
        ['Đ'] = "D",
        ['đ'] = "D",
        ['Ð'] = "D",
        ['ð'] = "D",
        ['Ł'] = "L",
        ['ł'] = "L",
        ['Ø'] = "O",
        ['ø'] = "O",
        ['Æ'] = "AE",
        ['æ'] = "AE",
        ['Œ'] = "OE",
        ['œ'] = "OE",
        ['Þ'] = "TH",
        ['þ'] = "TH",
        ['ß'] = "SS",
        ['ẞ'] = "SS",
        ['ı'] = "I",
        ['İ'] = "I",
    };

    /// <summary>
    /// The line of every guest of the stay, in order, or the reasons why the file cannot be built. A file is built only for
    /// complete data with every official code found; nothing is guessed, truncated or left blank to make it fit.
    /// </summary>
    public static AlloggiatiRecordFileResult Build(IReadOnlyList<AlloggiatiGuestRow> guests)
    {
        var issues = new List<AlloggiatiRecordFileIssue>();
        if (guests.Count == 0)
            issues.Add(new AlloggiatiRecordFileIssue(-1, AlloggiatiRecordFileIssueKind.NoGuests, null));
        if (guests.Count > MaxLines)
            issues.Add(new AlloggiatiRecordFileIssue(-1, AlloggiatiRecordFileIssueKind.TooManyGuests, null));

        var lines = new List<string>(guests.Count);
        foreach (var guest in guests)
        {
            var before = issues.Count;
            var line = BuildLine(guest, issues);
            if (issues.Count == before && line is not null)
                lines.Add(line);
        }

        return issues.Count == 0
            ? new AlloggiatiRecordFileResult(string.Join(LineBreak, lines), lines.Count, issues)
            : new AlloggiatiRecordFileResult(null, 0, issues);
    }

    /// <summary>
    /// A name as the portal accepts it: capital letters A-Z, spaces and apostrophes. Accents are removed (<c>José</c> →
    /// <c>JOSE</c>), a few letters are transliterated (<c>ß</c> → <c>SS</c>), hyphens become spaces, dots and commas are
    /// dropped. Null when the name has a character that cannot be written that way (other alphabets, digits, symbols), is
    /// empty, or longer than <paramref name="maxLength"/> afterwards: it is never silently cut or corrupted.
    /// </summary>
    /// <remarks>
    /// The manuals say only "UTF-8" and no allowed characters. The restriction to A-Z, space and apostrophe is what a
    /// third-party client that sends to the real portal reports it rejects otherwise ("Cognome con caratteri non validi"):
    /// level T in <c>.claude/context/regulations/alloggiati.md</c>, to be confirmed with the first <c>Test</c> call on a
    /// real account.
    /// </remarks>
    public static string? ToRecordName(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                builder.Append(char.ToUpperInvariant(c));
            else if (Letters.TryGetValue(c, out var replacement))
                builder.Append(replacement);
            else if (c is '\'' or '’' or '‘' or '`' or '´' or 'ʼ')
                builder.Append('\'');
            else if (char.IsWhiteSpace(c) || c is '-' or '‐' or '‑' or '–' or '—')
                builder.Append(' ');
            else if (c is '.' or ',')
                continue;
            else
                return null;
        }

        var name = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return name.Length == 0 || name.Length > maxLength ? null : name;
    }

    private static string? BuildLine(AlloggiatiGuestRow guest, List<AlloggiatiRecordFileIssue> issues)
    {
        void Issue(AlloggiatiRecordFileIssueKind kind, string? field = null) =>
            issues.Add(new AlloggiatiRecordFileIssue(guest.Position, kind, field));

        var failed = false;
        if (guest.MissingFields.Count > 0 || guest.CompositionIssue is not null)
        {
            Issue(AlloggiatiRecordFileIssueKind.DataIncomplete, guest.MissingFields.FirstOrDefault());
            failed = true;
        }

        foreach (var field in guest.CodesToComplete)
        {
            Issue(AlloggiatiRecordFileIssueKind.CodeToComplete, field);
            failed = true;
        }

        if (guest.StayDays is < 1 or > AlloggiatiTerms.MaxStayDaysPerSchedina)
        {
            Issue(AlloggiatiRecordFileIssueKind.StayDaysOutOfRange);
            failed = true;
        }

        var lastName = ToRecordName(guest.LastName, AlloggiatiRecordRules.MaxLastNameLength);
        if (lastName is null)
        {
            Issue(AlloggiatiRecordFileIssueKind.NameNotRepresentable, AlloggiatiRecordRules.FieldLastName);
            failed = true;
        }

        var firstName = ToRecordName(guest.FirstName, AlloggiatiRecordRules.MaxFirstNameLength);
        if (firstName is null)
        {
            Issue(AlloggiatiRecordFileIssueKind.NameNotRepresentable, AlloggiatiRecordRules.FieldFirstName);
            failed = true;
        }

        if (failed)
            return null;

        // Past the checks above every value is present; a field that still cannot be written reports the field.
        var line = new StringBuilder(LineLength);
        var ok = true;

        bool Append(string? value, FieldSpan span, string field, Func<string, bool>? isValid = null)
        {
            var text = value ?? string.Empty;
            if (text.Length > span.Length || (isValid is not null && !isValid(text)))
            {
                Issue(AlloggiatiRecordFileIssueKind.DataIncomplete, field);
                ok = false;
                return false;
            }

            line.Append(text.PadRight(span.Length));
            return true;
        }

        Append(guest.Codes.Type, Layout[0], AlloggiatiRecordRules.FieldType, v => AlloggiatiRecordRules.IsValidCodeShape(AlloggiatiCodeTable.TipiAlloggiato, v));
        Append(guest.ArrivalDate.ToString(DateFormat, CultureInfo.InvariantCulture), Layout[1], "arrivalDate");
        Append(guest.StayDays.ToString("00", CultureInfo.InvariantCulture), Layout[2], "stayDays");
        Append(lastName, Layout[3], AlloggiatiRecordRules.FieldLastName);
        Append(firstName, Layout[4], AlloggiatiRecordRules.FieldFirstName);
        Append(
            guest.Gender switch { Gender.Male => "1", Gender.Female => "2", _ => null },
            Layout[5],
            AlloggiatiRecordRules.FieldGender,
            v => v.Length == 1);
        Append(guest.DateOfBirth?.ToString(DateFormat, CultureInfo.InvariantCulture), Layout[6], AlloggiatiRecordRules.FieldDateOfBirth, v => v.Length == 10);

        if (guest.BornInItaly == true)
        {
            Append(guest.Codes.BirthComune, Layout[7], AlloggiatiRecordRules.FieldBirthComune, v => AlloggiatiRecordRules.IsValidCodeShape(AlloggiatiCodeTable.Comuni, v));
            Append(guest.BirthProvince?.Trim().ToUpperInvariant(), Layout[8], AlloggiatiRecordRules.FieldBirthProvince, AlloggiatiRecordRules.IsValidProvince);
        }
        else
        {
            // Born abroad: "il campo va comunque completato con spazi bianchi" (MANUALEALBERGHI.pdf, paragraph 12).
            line.Append(' ', Layout[7].Length + Layout[8].Length);
        }

        Append(guest.Codes.BirthCountry, Layout[9], AlloggiatiRecordRules.FieldBirthCountry, v => AlloggiatiRecordRules.IsValidCodeShape(AlloggiatiCodeTable.Stati, v));
        Append(guest.Codes.Citizenship, Layout[10], AlloggiatiRecordRules.FieldCitizenship, v => AlloggiatiRecordRules.IsValidCodeShape(AlloggiatiCodeTable.Stati, v));

        if (guest.RequiresDocument)
        {
            Append(guest.Codes.DocumentType, Layout[11], AlloggiatiRecordRules.FieldDocumentType, v => AlloggiatiRecordRules.IsValidCodeShape(AlloggiatiCodeTable.Documenti, v));
            Append(
                AlloggiatiRecordRules.NormalizeDocumentNumber(guest.DocumentNumber),
                Layout[12],
                AlloggiatiRecordRules.FieldDocumentNumber,
                AlloggiatiRecordRules.IsValidDocumentNumber);
            Append(guest.Codes.DocumentIssuePlace, Layout[13], AlloggiatiRecordRules.FieldDocumentIssuePlace, v => AlloggiatiRecordRules.IsValidCodeShape(AlloggiatiCodeTable.Comuni, v));
        }
        else
        {
            // Family and group members: the three document fields (5 + 20 + 9 characters) are blank.
            line.Append(' ', Layout[11].Length + Layout[12].Length + Layout[13].Length);
        }

        if (ok && line.Length != LineLength)
        {
            // Unreachable while Layout and the appends above agree: never emit a line the portal would shift.
            Issue(AlloggiatiRecordFileIssueKind.DataIncomplete);
            ok = false;
        }

        return ok ? line.ToString() : null;
    }
}

/// <summary>Outcome of <see cref="AlloggiatiRecordFile.Build"/>: the text and its line count, or the issues that block it.</summary>
public sealed record AlloggiatiRecordFileResult(string? Text, int LineCount, IReadOnlyList<AlloggiatiRecordFileIssue> Issues)
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public bool Success => Issues.Count == 0 && Text is not null;

    /// <summary>The file as bytes: UTF-8 without byte order mark (the manual asks for UTF-8 and a line starts at position 0).</summary>
    public byte[] ToBytes() => Utf8WithoutBom.GetBytes(Text ?? string.Empty);
}

/// <param name="Position">Position of the guest at fault (0-based, as in the summary); -1 when it concerns the whole stay.</param>
/// <param name="Field">Record field at fault (camelCase, <c>AlloggiatiRecordRules.Field*</c>), when there is one.</param>
public sealed record AlloggiatiRecordFileIssue(int Position, AlloggiatiRecordFileIssueKind Kind, string? Field);

/// <summary>Why the record file cannot be built.</summary>
public enum AlloggiatiRecordFileIssueKind
{
    /// <summary>No guest is registered for the stay.</summary>
    NoGuests,

    /// <summary>More guests than lines of a file.</summary>
    TooManyGuests,

    /// <summary>A guest lacks a field of the record, or does not fit the order of the stay.</summary>
    DataIncomplete,

    /// <summary>The official code of a field is not found in the imported tables.</summary>
    CodeToComplete,

    /// <summary>The stay lasts less than 1 or more than 30 days: one schedina cannot hold it.</summary>
    StayDaysOutOfRange,

    /// <summary>The name cannot be written with the letters the portal accepts.</summary>
    NameNotRepresentable,
}
