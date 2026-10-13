namespace Casazen.Core.Regulatory;

/// <summary>Kind of a valid Italian fiscal code (<see cref="ItalianFiscalCode"/>).</summary>
public enum ItalianFiscalCodeKind
{
    /// <summary>16 characters of a natural person (D.M. 23/12/1976), possibly with omocodia substitutions.</summary>
    Person,

    /// <summary>
    /// 11 digits with the check digit: the fiscal code of a non-natural person (company, association), equal to its VAT
    /// number when it has one, or the provisional code of a natural person.
    /// </summary>
    Numeric,
}

/// <summary>
/// Validation of the Italian fiscal code (codice fiscale) of a party (LT-14, A7-28): the single place that knows the
/// format. Frontend mirror: <c>src/lib/fiscal-code.ts</c>.
/// <list type="bullet">
/// <item>Natural person, 16 characters (D.M. 23/12/1976 and D.M. 12/03/1974 for the omocodia): 6 letters (surname and
/// name), 2 digits (year), the month letter (<c>ABCDEHLMPRST</c>), 2 digits (day, +40 for women), the cadastral code of
/// the place of birth (a letter and 3 digits) and the check character. In case of omocodia the digits are replaced, from
/// the right, by <c>LMNPQRSTUV</c> (0-9): the check character is computed on the code as written.</item>
/// <item>11 digits: the last one is the check digit of the first ten (odd positions as they are, even positions doubled
/// minus 9 when above 9; check = (10 - sum mod 10) mod 10).</item>
/// </list>
/// A code is checked after <see cref="Normalize"/> (spaces removed, upper case). The check does not prove that the code
/// was issued by the Agenzia delle Entrate: only its format and check character.
/// </summary>
public static class ItalianFiscalCode
{
    /// <summary>Letters that replace the digits 0-9 in an omocodia.</summary>
    private const string OmocodiaLetters = "LMNPQRSTUV";

    private const string MonthLetters = "ABCDEHLMPRST";

    /// <summary>Positions (0-based) of the 16-character code that hold a digit, or its omocodia letter.</summary>
    private static readonly int[] DigitPositions = [6, 7, 9, 10, 12, 13, 14];

    /// <summary>Values of the characters in odd positions (1st, 3rd, ... 15th) for the check character.</summary>
    private static readonly int[] OddValues =
    [
        // A  B  C  D  E   F   G   H   I   J  K  L   M   N   O  P  Q  R   S   T   U   V   W   X   Y   Z
        1, 0, 5, 7, 9, 13, 15, 17, 19, 21, 2, 4, 18, 20, 11, 3, 6, 8, 12, 14, 16, 10, 22, 25, 24, 23,
    ];

    /// <summary>Upper case, without spaces of any kind; null becomes empty.</summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return string.Empty;

        var chars = code.Where(c => !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray();
        return new string(chars);
    }

    /// <summary>The kind of the code once normalized, or null when it is not a valid fiscal code.</summary>
    public static ItalianFiscalCodeKind? Classify(string? code)
    {
        var normalized = Normalize(code);
        if (IsValidPersonCode(normalized))
            return ItalianFiscalCodeKind.Person;
        if (IsValidNumericCode(normalized))
            return ItalianFiscalCodeKind.Numeric;
        return null;
    }

    /// <summary>True when the normalized code is a valid 16-character or 11-digit fiscal code.</summary>
    public static bool IsValid(string? code) => Classify(code) is not null;

    /// <summary>16-character code of a natural person, already normalized: structure, month, day and check character.</summary>
    public static bool IsValidPersonCode(string code)
    {
        if (code.Length != 16)
            return false;

        for (var i = 0; i < 16; i++)
        {
            var c = code[i];
            var isDigitPosition = Array.IndexOf(DigitPositions, i) >= 0;
            var valid = isDigitPosition
                ? IsAsciiDigit(c) || OmocodiaLetters.Contains(c)
                : IsAsciiLetter(c);
            if (!valid)
                return false;
        }

        if (!MonthLetters.Contains(code[8]))
            return false;

        var day = DigitValue(code[9]) * 10 + DigitValue(code[10]);
        if (day is < 1 or > 31 and < 41 or > 71)
            return false;

        return code[15] == CheckCharacter(code.AsSpan(0, 15));
    }

    /// <summary>11-digit code, already normalized: digits only and check digit.</summary>
    public static bool IsValidNumericCode(string code)
    {
        if (code.Length != 11 || !code.All(IsAsciiDigit))
            return false;

        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var digit = code[i] - '0';
            if (i % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }

            sum += digit;
        }

        return code[10] - '0' == (10 - sum % 10) % 10;
    }

    /// <summary>Check character of the first 15 characters of a 16-character code (upper case, ASCII).</summary>
    public static char CheckCharacter(ReadOnlySpan<char> first15)
    {
        var sum = 0;
        for (var i = 0; i < first15.Length; i++)
        {
            var c = first15[i];
            var index = IsAsciiDigit(c) ? c - '0' : c - 'A';
            // 1st, 3rd, ... character (odd position counting from 1): odd table; digits count as the letter of the same index.
            sum += i % 2 == 0 ? OddValues[index] : index;
        }

        return (char)('A' + sum % 26);
    }

    private static int DigitValue(char c) => IsAsciiDigit(c) ? c - '0' : OmocodiaLetters.IndexOf(c);

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z';
}
