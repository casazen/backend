using System.Globalization;
using System.Text;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The check-in data of the guest who must be verified, as the MRZ is compared with it (E03/F04/US02/T02). Read from the stay guest (or the
/// booker); never stored by the comparison.
/// </summary>
/// <param name="FirstName">The first names as entered.</param>
/// <param name="LastName">The surnames as entered.</param>
/// <param name="DateOfBirth">The date of birth, when known.</param>
/// <param name="DocumentNumber">The document number, when entered.</param>
/// <param name="Citizenship">The citizenship as entered (a label, or an ISO code).</param>
public sealed record CheckInIdentity(string? FirstName, string? LastName, DateOnly? DateOfBirth, string? DocumentNumber, string? Citizenship);

/// <summary>
/// Whether each field of the MRZ agrees with the check-in data: true agrees, false disagrees, null not compared (the check-in has no value for
/// it, or no reliable form to compare). One flag per field, never the values.
/// </summary>
public sealed record MrzConsistency(bool? Names, bool? BirthDate, bool? DocumentNumber, bool? Nationality);

/// <summary>
/// Compares the MRZ of a document with the check-in data of the guest (E03/F04/US02/T02, AC-NF51-3, AC-NF51-4) after normalization: case,
/// diacritics (and their German transliteration, Ä = AE), fillers and separators, and the order of the names.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Names</b>: the tokens of surname and given names on one side and of first and last names on the other. They agree when the tokens of
/// one side are all on the other (the guest left out a middle name, or the MRZ cut a long name), and the side found has at least two tokens
/// (or both sides have one): a surname alone never matches. A token matches an equal one, or one it is the start of when the shorter has four
/// letters or more (a name cut by the MRZ). No names on either side: not consistent (a person is never matched on nothing).</item>
/// <item><b>Birth date</b> and <b>document number</b> (letters and digits only): compared when both are known.</item>
/// <item><b>Nationality</b>: the MRZ has the ISO 3166 alpha-3 code; the check-in keeps the citizenship as an Alloggiati code and a label, and
/// CasaZen has no table from one to the other. It is compared only when the label is itself a code of three letters; otherwise it is not
/// compared (never guessed from a free text).</item>
/// </list>
/// </remarks>
public static class MrzCheckInConsistency
{
    /// <summary>The flags of the comparison of <paramref name="mrz"/> with <paramref name="checkIn"/>.</summary>
    public static MrzConsistency Compare(MrzExtractionResult mrz, CheckInIdentity checkIn)
    {
        ArgumentNullException.ThrowIfNull(mrz);
        ArgumentNullException.ThrowIfNull(checkIn);

        return new MrzConsistency(
            NamesAgree(mrz.Surname, mrz.GivenNames, checkIn.FirstName, checkIn.LastName),
            mrz.DateOfBirth is { } born && checkIn.DateOfBirth is { } declared ? DateOnly.FromDateTime(born) == declared : null,
            DocumentNumbersAgree(mrz.DocumentNumber, checkIn.DocumentNumber),
            NationalitiesAgree(mrz.Nationality, checkIn.Citizenship));
    }

    internal static bool NamesAgree(string? surname, string? givenNames, string? firstName, string? lastName)
    {
        var onDocument = Tokens($"{surname} {givenNames}", transliterate: false);
        if (onDocument.Count == 0)
            return false;

        foreach (var transliterate in new[] { false, true })
        {
            var declared = Tokens($"{firstName} {lastName}", transliterate);
            if (declared.Count == 0)
                return false;

            if (Covers(declared, onDocument) || Covers(onDocument, declared))
                return true;
        }

        return false;
    }

    internal static bool? DocumentNumbersAgree(string? onDocument, string? declared)
    {
        var a = Alphanumeric(onDocument);
        var b = Alphanumeric(declared);
        return a.Length == 0 || b.Length == 0 ? null : string.Equals(a, b, StringComparison.Ordinal);
    }

    internal static bool? NationalitiesAgree(string? onDocument, string? declared)
    {
        var code = Letters(onDocument);
        var label = Letters(declared);
        return code.Length == 0 || label.Length != 3 ? null : string.Equals(code, label, StringComparison.Ordinal);
    }

    /// <summary>Every token of <paramref name="part"/> is in <paramref name="whole"/>, and <paramref name="part"/> is enough to name a person.</summary>
    private static bool Covers(IReadOnlyList<string> part, IReadOnlyList<string> whole)
    {
        var enough = part.Count >= 2 || (part.Count == 1 && whole.Count == 1);
        return enough && part.All(token => whole.Any(other => SameName(token, other)));
    }

    private static bool SameName(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return true;

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 4 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    /// <summary>The names in capital letters A to Z, without diacritics, split on anything else (the fillers of the MRZ, hyphens, apostrophes).</summary>
    internal static IReadOnlyList<string> Tokens(string? value, bool transliterate)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

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

        var letters = new StringBuilder(upper.Length);
        foreach (var ch in upper.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            letters.Append(ch is >= 'A' and <= 'Z' ? ch : ' ');
        }

        return letters.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
    }

    private static string Alphanumeric(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.ToUpperInvariant().Where(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9').ToArray());

    private static string Letters(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Trim().ToUpperInvariant().Where(ch => ch is >= 'A' and <= 'Z').ToArray());
}
