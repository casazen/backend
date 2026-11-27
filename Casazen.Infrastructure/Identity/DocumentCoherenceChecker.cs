using System.Globalization;
using System.Text;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// What the checker needs to know about the stay and the guest (E71/F01/US02/T02): the day the document must still be valid on, and what the guest typed in the
/// data step. Nothing here is read from the document.
/// </summary>
/// <param name="CheckInDate">
/// The calendar day of the check-in of the stay (a date, not an instant: the caller takes it from <c>RomeCalendar</c> on the injected <c>TimeProvider</c>). A document
/// that expires before this day is expired; a birth after it, or more than 120 years before it, is impossible.
/// </param>
/// <param name="Declared">The first and last names, the date of birth, the document number and the citizenship the guest typed, or null while nothing is typed.</param>
public sealed record DocumentCoherenceContext(DateOnly CheckInDate, CheckInIdentity? Declared = null);

/// <summary>
/// The coherence of the fields read on a document (E71/F01/US02/T02, NF-190, NF-191, decision D11): whether the MRZ and the visual zone agree on the fields that
/// identify the holder, whether the dates are possible and the document still valid on the day of the stay, whether the check digits of the MRZ hold, and whether the
/// document agrees with what the guest typed. A coherent photo can still show an altered document: this is coherence only, and where a cryptographic proof is required
/// the photo alone goes to review (the segments of E70). <b>It decides nothing about the outcome</b> (the engine of E73 maps an identifying conflict and an invalid MRZ
/// to a review) and <b>it never returns or logs a value</b>: the report has check names, results and field names, and the class has no logger.
/// <para>
/// Pure and without state: no I/O, no clock (the day comes with the context), no configuration; so it is a singleton and a test builds it with <c>new</c>.
/// </para>
/// <para>
/// The flags and the codes of the report are derived from its entries (<see cref="DocumentCoherenceReport.FromChecks"/>): an <c>unknown</c> entry leaves its flag
/// unknown (<c>null</c>), never «ok», and <see cref="DocumentCoherenceReport.AllPassed"/> is the one answer to whether the checks a format requires all passed.
/// </para>
/// <list type="bullet">
/// <item><b>mrz_check_digits</b>: passed when the MRZ was read and every ICAO 9303 check digit holds, failed when one fails, unknown when no MRZ was read.</item>
/// <item><b>expiry</b>: failed when the date of expiry (the MRZ one, digit-protected, else the printed one) is before the day of the stay; the day itself is valid.</item>
/// <item><b>side_consistency</b>: for every field of <c>policy.Document.IdentifyingFields</c>, the value of the MRZ and the value of the visual zone (or two printed
/// sides) must agree: names after the normalization of the MRZ (upper case, no diacritics, apostrophes, hyphens and spaces all the same as the filler; the German
/// transliteration too; an MRZ name field cut at its length is not a conflict), dates as dates (the printed one has its century, the MRZ one has it inferred), the
/// document number as letters and digits. A field read on one side only is unknown, never a conflict.</item>
/// <item><b>field_coherence</b>: the dates are possible: the birth is not after the day of the stay and not more than 120 years before it, the issue is not after
/// the expiry.</item>
/// <item><b>booking_consistency</b>: the flags of <see cref="MrzCheckInConsistency"/> (names, birth date, document number, nationality), as they are.</item>
/// </list>
/// </summary>
public sealed class DocumentCoherenceChecker
{
    /// <summary>
    /// The most years a holder can have lived on the day of the stay for the birth date to be believed. A bound of plausibility of a date (it sets a flag the engine
    /// reads, never approves or rejects a person), fixed by the brief of the check; the policy file has no member for it.
    /// </summary>
    private const int MaxPlausibleAgeYears = 120;

    /// <summary>The length of the name field of an MRZ line of each type (ICAO 9303): TD1 line 3, TD2 line 1 after the code and the state, TD3 line 1 after them.</summary>
    private static readonly Dictionary<string, int> MrzNameFieldLengths = new(StringComparer.Ordinal)
    {
        [DocumentFormatMrzTypes.Td1] = 30,
        [DocumentFormatMrzTypes.Td2] = 31,
        [DocumentFormatMrzTypes.Td3] = 39,
    };

    private enum Kind
    {
        Name,
        Date,
        Number,
        Letters,
        Other,
    }

    /// <summary>The checks of the document, in the order of <see cref="DocumentFormatChecks.All"/>.</summary>
    /// <param name="fields">The values read, by zone and by MRZ (<see cref="ZonedFieldReader"/>), of every side captured so far.</param>
    /// <param name="mrz">The reading of the MRZ with its check digits, or null when none was read.</param>
    /// <param name="context">The day of the stay and what the guest typed.</param>
    /// <param name="format">The format of the document (the type of its MRZ says how long its name field is).</param>
    /// <param name="policy">The decision policy: which fields identify the holder.</param>
    public DocumentCoherenceReport Check(
        IReadOnlyList<ZonedField> fields, MrzReading? mrz, DocumentCoherenceContext context, DocumentFormat format, IdentityDecisionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(policy);

        var machine = MachineValues(fields, mrz?.Result);
        var printed = PrintedValues(fields);
        var checks = new List<DocumentCoherenceCheck>();

        // mrz_check_digits: nothing proves an MRZ that was not read, so nothing is assumed.
        var digits = mrz?.Result is not { } read ? DocumentCoherenceResults.Unknown : read.CheckDigitsValid ? DocumentCoherenceResults.Passed : DocumentCoherenceResults.Failed;
        checks.Add(new DocumentCoherenceCheck(DocumentFormatChecks.MrzCheckDigits, digits));

        // expiry: the MRZ date first (its own digit protects it), then the printed one; the day of the expiry is still valid.
        var expiry = DateOf(machine, ZonedFieldNames.ExpiryDate) ?? DateOf(printed, ZonedFieldNames.ExpiryDate);
        var expired = expiry is { } expires && expires < context.CheckInDate;
        checks.Add(new DocumentCoherenceCheck(
            DocumentFormatChecks.Expiry,
            expiry is null ? DocumentCoherenceResults.Unknown : expired ? DocumentCoherenceResults.Failed : DocumentCoherenceResults.Passed,
            ZonedFieldNames.ExpiryDate));

        // side_consistency: the fields that identify the holder, one entry each.
        var nameFieldIsFull = NameFieldIsFull(machine, format);
        var identifying = policy.Document.IdentifyingFields.Distinct(StringComparer.Ordinal).ToList();
        foreach (var name in identifying)
        {
            var result = CompareSides(name, machine, printed, MayBeCut(name, machine, nameFieldIsFull));
            checks.Add(new DocumentCoherenceCheck(DocumentFormatChecks.SideConsistency, result, name));
        }

        // A policy that names no field is not a policy that compares nothing and passes: the check stays unknown.
        if (identifying.Count == 0)
            checks.Add(new DocumentCoherenceCheck(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.Unknown));

        // field_coherence: the dates are possible. The printed birth date has its century, the MRZ one has it inferred.
        var birth = DateOf(printed, ZonedFieldNames.BirthDate) ?? DateOf(machine, ZonedFieldNames.BirthDate);
        var birthResult = birth is not { } born
            ? DocumentCoherenceResults.Unknown
            : born > context.CheckInDate || AgeOn(born, context.CheckInDate) > MaxPlausibleAgeYears
                ? DocumentCoherenceResults.Failed
                : DocumentCoherenceResults.Passed;
        var issue = DateOf(printed, ZonedFieldNames.IssueDate);
        var issueResult = issue is not { } issued || expiry is not { } until
            ? DocumentCoherenceResults.Unknown
            : issued > until ? DocumentCoherenceResults.Failed : DocumentCoherenceResults.Passed;
        checks.Add(new DocumentCoherenceCheck(DocumentFormatChecks.FieldCoherence, birthResult, ZonedFieldNames.BirthDate));
        checks.Add(new DocumentCoherenceCheck(DocumentFormatChecks.FieldCoherence, issueResult, ZonedFieldNames.IssueDate));

        // booking_consistency: MrzCheckInConsistency, reused as it is: true agrees, false disagrees, null was not compared.
        var booking = mrz?.Result is { } machineRead && context.Declared is { } declared
            ? MrzCheckInConsistency.Compare(machineRead, declared)
            : new MrzConsistency(null, null, null, null);
        checks.Add(new DocumentCoherenceCheck(DocumentFormatChecks.BookingConsistency, Flag(booking.Names), DocumentCoherenceFields.Names));
        checks.Add(new DocumentCoherenceCheck(DocumentFormatChecks.BookingConsistency, Flag(booking.BirthDate), ZonedFieldNames.BirthDate));
        checks.Add(new DocumentCoherenceCheck(DocumentFormatChecks.BookingConsistency, Flag(booking.DocumentNumber), ZonedFieldNames.DocumentNumber));
        checks.Add(new DocumentCoherenceCheck(DocumentFormatChecks.BookingConsistency, Flag(booking.Nationality), ZonedFieldNames.Nationality));

        // The flags and the codes come from the entries and from nothing else: an unknown entry makes a flag unknown, never «ok».
        return DocumentCoherenceReport.FromChecks(checks);
    }

    /// <summary>
    /// The values of the MRZ by field name: the reading with its check digits when there is one (a field whose own digit failed is not in it), the fields of the list
    /// that come from the MRZ otherwise.
    /// </summary>
    private static Dictionary<string, List<string>> MachineValues(IReadOnlyList<ZonedField> fields, MrzExtractionResult? read)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (read is null)
        {
            foreach (var field in fields.Where(field => field.Source == ZonedFieldSources.Mrz))
                Put(values, field.Name, field.Value);

            return values;
        }

        Put(values, ZonedFieldNames.Surname, read.Surname);
        Put(values, ZonedFieldNames.GivenNames, read.GivenNames);
        Put(values, ZonedFieldNames.BirthDate, Day(read.DateOfBirth));
        Put(values, ZonedFieldNames.Sex, read.Sex);
        Put(values, ZonedFieldNames.Nationality, read.Nationality);
        Put(values, ZonedFieldNames.DocumentNumber, read.DocumentNumber);
        Put(values, ZonedFieldNames.ExpiryDate, Day(read.ExpirationDate));
        return values;
    }

    /// <summary>The values of the visual zone by field name, in the order they were read (a field printed on both sides has two).</summary>
    private static Dictionary<string, List<string>> PrintedValues(IReadOnlyList<ZonedField> fields)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var field in fields.Where(field => field.Source == ZonedFieldSources.Viz))
            Put(values, field.Name, field.Value);

        return values;
    }

    private static void Put(Dictionary<string, List<string>> values, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!values.TryGetValue(name, out var list))
            values[name] = list = [];

        list.Add(value);
    }

    /// <summary>
    /// Whether the values of one field read on different sides agree. The MRZ value, when there is one, is the reference. Passed when there are at least two usable
    /// values and all agree, failed when two usable values disagree, unknown when fewer than two can be compared. <paramref name="referenceMayBeCut"/>: the MRZ value
    /// can be the beginning of a longer name (the line ended inside it).
    /// </summary>
    private static string CompareSides(
        string name, Dictionary<string, List<string>> machine, Dictionary<string, List<string>> printed, bool referenceMayBeCut)
    {
        var kind = KindOf(name);
        var values = new List<(string Value, bool FromMachine)>();
        if (machine.TryGetValue(name, out var fromMachine))
            values.AddRange(fromMachine.Take(1).Where(value => Usable(kind, value)).Select(value => (value, true)));

        if (printed.TryGetValue(name, out var fromPrint))
            values.AddRange(fromPrint.Where(value => Usable(kind, value)).Select(value => (value, false)));

        if (values.Count < 2)
            return DocumentCoherenceResults.Unknown;

        var reference = values[0];
        foreach (var other in values.Skip(1))
        {
            if (!Agree(kind, reference.Value, other.Value, reference.FromMachine && referenceMayBeCut))
                return DocumentCoherenceResults.Failed;
        }

        return DocumentCoherenceResults.Passed;
    }

    private static bool Agree(Kind kind, string reference, string other, bool referenceMayBeCut)
    {
        switch (kind)
        {
            case Kind.Name:
                // The MRZ spells with A to Z only, and some issuers transliterate (the umlaut as two letters): the printed name must equal it in one of the two ways.
                foreach (var transliterate in new[] { false, true })
                {
                    var wanted = NameKey(reference, transliterate);
                    var found = NameKey(other, transliterate);
                    if (wanted == found || (referenceMayBeCut && found.Length > wanted.Length && found.StartsWith(wanted, StringComparison.Ordinal)))
                        return true;
                }

                return false;
            case Kind.Date:
                return TryDate(reference, out var first) && TryDate(other, out var second) && first == second;
            case Kind.Number:
                return MrzCheckInConsistency.DocumentNumbersAgree(reference, other) is true;
            case Kind.Letters:
                return string.Equals(LettersOnly(reference), LettersOnly(other), StringComparison.Ordinal);
            default:
                return string.Equals(AlphanumericOnly(reference), AlphanumericOnly(other), StringComparison.Ordinal);
        }
    }

    private static bool Usable(Kind kind, string value) => kind switch
    {
        Kind.Name => NameKey(value, transliterate: false).Length > 0,
        Kind.Date => TryDate(value, out _),
        Kind.Letters => LettersOnly(value).Length > 0,
        _ => AlphanumericOnly(value).Length > 0,
    };

    private static Kind KindOf(string name) => name switch
    {
        ZonedFieldNames.Surname or ZonedFieldNames.GivenNames => Kind.Name,
        ZonedFieldNames.BirthDate or ZonedFieldNames.IssueDate or ZonedFieldNames.ExpiryDate => Kind.Date,
        ZonedFieldNames.DocumentNumber => Kind.Number,
        ZonedFieldNames.Sex or ZonedFieldNames.Nationality => Kind.Letters,
        _ => Kind.Other,
    };

    /// <summary>
    /// Whether the name field of the MRZ is as long as its line allows, so a name that is shorter than the printed one may have been cut. The length is rebuilt from
    /// the surname and the given names, which the reading splits at the double filler (one filler between the words of a name is one space).
    /// </summary>
    private static bool NameFieldIsFull(Dictionary<string, List<string>> machine, DocumentFormat format)
    {
        if (!MrzNameFieldLengths.TryGetValue(format.Mrz.Type, out var capacity) || !machine.TryGetValue(ZonedFieldNames.Surname, out var surnames))
            return false;

        var length = surnames[0].Length;
        if (machine.TryGetValue(ZonedFieldNames.GivenNames, out var givenNames))
            length += 2 + givenNames[0].Length;

        return length >= capacity;
    }

    /// <summary>
    /// Whether the MRZ value of <paramref name="name"/> can be the beginning of the printed one: the given names when the name field is full (it ends inside them), the
    /// surname only when the field is full and holds no given names at all (it ends inside the surname, so nothing of the given names is left).
    /// </summary>
    private static bool MayBeCut(string name, Dictionary<string, List<string>> machine, bool nameFieldIsFull) => name switch
    {
        ZonedFieldNames.GivenNames => nameFieldIsFull,
        ZonedFieldNames.Surname => nameFieldIsFull && !machine.ContainsKey(ZonedFieldNames.GivenNames),
        _ => false,
    };

    /// <summary>
    /// A name as the MRZ writes it, squashed: upper case, the diacritics removed, and everything that is not a letter A to Z (the filler, spaces, hyphens, apostrophes)
    /// dropped, so that the separators the issuer chose make no difference. With <paramref name="transliterate"/> the letters the MRZ spells with two (the umlauts, the
    /// sharp s, the ring and the slashed letters) are spelled so (the rule of <see cref="MrzCheckInConsistency"/>).
    /// </summary>
    private static string NameKey(string? value, bool transliterate)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var upper = value.ToUpperInvariant();
        if (transliterate)
        {
            upper = upper
                .Replace("Ä", "AE", StringComparison.Ordinal)
                .Replace("Ö", "OE", StringComparison.Ordinal)
                .Replace("Ü", "UE", StringComparison.Ordinal)
                .Replace("ß", "SS", StringComparison.Ordinal)
                .Replace("Å", "AA", StringComparison.Ordinal)
                .Replace("Æ", "AE", StringComparison.Ordinal)
                .Replace("Ø", "OE", StringComparison.Ordinal);
        }

        var key = new StringBuilder(upper.Length);
        foreach (var ch in upper.Normalize(NormalizationForm.FormD))
        {
            if (ch is >= 'A' and <= 'Z')
                key.Append(ch);
        }

        return key.ToString();
    }

    private static string LettersOnly(string value) => new(value.ToUpperInvariant().Where(ch => ch is >= 'A' and <= 'Z').ToArray());

    private static string AlphanumericOnly(string value) => new(value.ToUpperInvariant().Where(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9').ToArray());

    private static string? Day(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>The first value of the field that is a date, or null.</summary>
    private static DateOnly? DateOf(Dictionary<string, List<string>> values, string name) =>
        values.TryGetValue(name, out var list) && list.FirstOrDefault(value => TryDate(value, out _)) is { } text && TryDate(text, out var date) ? date : null;

    /// <summary>The age in whole years on <paramref name="day"/> of someone born on <paramref name="born"/> (the 29th of February counts on the 28th in the other years).</summary>
    private static int AgeOn(DateOnly born, DateOnly day)
    {
        var age = day.Year - born.Year;
        var birthday = new DateOnly(day.Year, born.Month, Math.Min(born.Day, DateTime.DaysInMonth(day.Year, born.Month)));
        return day < birthday ? age - 1 : age;
    }

    private static string Flag(bool? agrees) => agrees switch
    {
        true => DocumentCoherenceResults.Passed,
        false => DocumentCoherenceResults.Failed,
        null => DocumentCoherenceResults.Unknown,
    };
}
