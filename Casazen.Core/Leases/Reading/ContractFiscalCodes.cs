using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Leases.Reading;

public readonly record struct FoundFiscalCode(string Code, int Index, int Length, bool Repaired);

public static partial class ContractFiscalCodes
{
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private const string OmocodiaLetters = "LMNPQRSTUV";

    private static readonly int[] DigitPositions = [6, 7, 9, 10, 12, 13, 14];

    private const string LettersLikeDigits = "0:OQD,1:IL,2:Z,4:A,5:S,6:G,7:T,8:B";

    private const string DigitsLikeLetters = "O:0,Q:0,D:0,I:1,Z:2,A:4,S:5,G:6,T:7,B:8";

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z0-9]{16}(?![A-Za-z0-9])", Options, 2000)]
    private static partial Regex Token();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z0-9]{17}(?![A-Za-z0-9])", Options, 2000)]
    private static partial Regex TokenWithOneTooMany();

    [GeneratedRegex(
        @"(?:\bc\s?\.?\s?f\s?\.?|\bcod(?:ice|\.)?\s*fisc(?:ale|\.)?)\s*[:.\-]?\s*(?<code>(?:[A-Za-z0-9]\s?){15}[A-Za-z0-9])(?![A-Za-z0-9])",
        Options | RegexOptions.IgnoreCase,
        2000)]
    private static partial Regex Labelled();

    public static IReadOnlyList<FoundFiscalCode> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var found = new List<FoundFiscalCode>();

        void Consider(string raw, int index, int length)
        {
            var code = new string(raw.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
            if (code.Length != 16 || found.Any(f => f.Code == code))
                return;

            if (ItalianFiscalCode.IsValidPersonCode(code))
                found.Add(new FoundFiscalCode(code, index, length, false));
            else if (TryRepair(code) is { } repaired && found.All(f => f.Code != repaired))
                found.Add(new FoundFiscalCode(repaired, index, length, true));
        }

        void ConsiderWithOneTooMany(string raw, int index, int length)
        {
            var printed = new string(raw.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
            if (printed.Length != 17)
                return;

            var candidates = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < printed.Length; i++)
            {
                var candidate = printed.Remove(i, 1);
                if (ItalianFiscalCode.IsValidPersonCode(candidate))
                    candidates.Add(candidate);
            }

            if (candidates.Count == 1 && candidates.First() is var code && found.All(f => f.Code != code))
                found.Add(new FoundFiscalCode(code, index, length, true));
        }

        var tokens = Token().Matches(text).Select(m => (m.Index, m.Length, m.Value, OneTooMany: false))
            .Concat(TokenWithOneTooMany().Matches(text).Select(m => (m.Index, m.Length, m.Value, OneTooMany: true)))
            .OrderBy(t => t.Index);
        foreach (var token in tokens)
        {
            if (token.OneTooMany)
                ConsiderWithOneTooMany(token.Value, token.Index, token.Length);
            else
                Consider(token.Value, token.Index, token.Length);
        }

        foreach (Match match in Labelled().Matches(text))
            Consider(match.Groups["code"].Value, match.Groups["code"].Index, match.Groups["code"].Length);

        return found.OrderBy(f => f.Index).ToList();
    }

    private static string? TryRepair(string code)
    {
        var wrong = new List<int>();
        for (var i = 0; i < 16; i++)
        {
            var c = code[i];
            var ok = Array.IndexOf(DigitPositions, i) >= 0
                ? c is >= '0' and <= '9' || OmocodiaLetters.Contains(c)
                : c is >= 'A' and <= 'Z';
            if (!ok)
                wrong.Add(i);
        }

        if (wrong.Count is 0 or > 2)
            return null;

        var choices = wrong
            .Select(i => Alternatives(code[i], letterPosition: Array.IndexOf(DigitPositions, i) < 0))
            .ToList();
        if (choices.Any(c => c.Count == 0))
            return null;

        var valid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var first in choices[0])
        {
            if (choices.Count == 1)
            {
                Try(code, wrong, [first], valid);
                continue;
            }

            foreach (var second in choices[1])
                Try(code, wrong, [first, second], valid);
        }

        return valid.Count == 1 ? valid.First() : null;
    }

    private static void Try(string code, List<int> positions, char[] replacements, HashSet<string> valid)
    {
        var candidate = code.ToCharArray();
        for (var i = 0; i < positions.Count; i++)
            candidate[positions[i]] = replacements[i];
        var text = new string(candidate);
        if (ItalianFiscalCode.IsValidPersonCode(text))
            valid.Add(text);
    }

    private static List<char> Alternatives(char printed, bool letterPosition)
    {
        var table = letterPosition ? LettersLikeDigits : DigitsLikeLetters;
        foreach (var entry in table.Split(','))
        {
            if (entry[0] == printed)
                return entry[2..].ToList();
        }

        return [];
    }

    public static string SurnameCode(string surname)
    {
        var (consonants, vowels) = Letters(surname);
        return Pad(consonants + vowels);
    }

    public static string NameCode(string name)
    {
        var (consonants, vowels) = Letters(name);
        return consonants.Length >= 4 ? $"{consonants[0]}{consonants[2]}{consonants[3]}" : Pad(consonants + vowels);
    }

    private static string Pad(string letters) => letters.Length >= 3 ? letters[..3] : letters.PadRight(3, 'X');

    private static (string Consonants, string Vowels) Letters(string text)
    {
        var consonants = new StringBuilder();
        var vowels = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            var upper = char.ToUpperInvariant(c);
            if (upper is < 'A' or > 'Z')
                continue;

            (upper is 'A' or 'E' or 'I' or 'O' or 'U' ? vowels : consonants).Append(upper);
        }

        return (consonants.ToString(), vowels.ToString());
    }
}
