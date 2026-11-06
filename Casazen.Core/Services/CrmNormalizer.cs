using System.Globalization;
using System.Net.Mail;
using System.Text;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The one place that turns what a person typed into the form the keyed index of the CRM hashes (E07/F01/US02/T01, NF-88, NF-89): an e-mail
/// address, a phone number, a name. The index is only as stable as the form it is given (<see cref="ICrmContactIndex"/>), so the matching, the
/// contacts and the suppression list all go through these functions and nowhere else. Pure: no clock, no database, no configuration read here.
/// </summary>
public static class CrmNormalizer
{
    /// <summary>Fewest digits a phone number can have, the calling code included: under it the text is not a phone number.</summary>
    public const int MinPhoneDigits = 6;

    /// <summary>Most digits of a phone number, the calling code included (E.164).</summary>
    public const int MaxPhoneDigits = 15;

    /// <summary>Longest e-mail address stored: the column of <c>Guest.Email</c> and of <c>MarketingConsentRecord.MarketingEmail</c>.</summary>
    public const int MaxEmailLength = 255;

    /// <summary>
    /// The e-mail address in the form the index uses: trimmed and lowercase, the rule of <see cref="ShowcaseBookingRules.NormalizeEmail"/>. An
    /// empty string for a missing address, never <c>null</c>.
    /// </summary>
    public static string NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? string.Empty : ShowcaseBookingRules.NormalizeEmail(email);

    /// <summary>
    /// True when <paramref name="email"/> is a plain address that can be a key: no display name, no spaces, a host with a dot, at most
    /// <see cref="MaxEmailLength"/> characters. <paramref name="normalized"/> is then the form of <see cref="NormalizeEmail"/>; otherwise it is
    /// empty. The placeholder of an anonymized guest is a well-formed address, so the caller skips an erased guest before it gets here.
    /// </summary>
    public static bool TryNormalizeEmail(string? email, out string normalized)
    {
        normalized = string.Empty;
        var candidate = NormalizeEmail(email);
        if (candidate.Length == 0
            || candidate.Length > MaxEmailLength
            || candidate.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !MailAddress.TryCreate(candidate, out var parsed)
            || !string.IsNullOrEmpty(parsed.DisplayName)
            || !string.Equals(parsed.Address, candidate, StringComparison.OrdinalIgnoreCase)
            || !parsed.Host.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    /// <summary>
    /// The phone number as E.164 with a leading plus (<c>+393331234567</c>), or <c>null</c> when the text is not a phone number. Only the digits
    /// count: <c>+39 333 123 4567</c>, <c>0039 333-1234567</c> and <c>(+39) 3331234567</c> are one number. A number that starts with a plus keeps
    /// its calling code, <c>00</c> at the start becomes the plus, and a national number takes <paramref name="defaultCallingCode"/> (default
    /// <see cref="CrmOptions.DefaultCallingCodeValue"/>, <c>Crm:DefaultCallingCode</c>); a leading zero of a national number is kept, as the
    /// Italian landlines need it. <c>null</c> under <see cref="MinPhoneDigits"/> digits or over <see cref="MaxPhoneDigits"/>, and for a
    /// placeholder: a number whose national part is one digit repeated (<c>+39 0000000000</c>, what the demo and some forms put in the box) is
    /// not a phone, and two guests who both left it must never be taken for the same person.
    /// </summary>
    public static string? NormalizePhone(string? phone, string? defaultCallingCode = null)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var text = phone.Trim();
        var international = text.TrimStart('(', ' ').StartsWith('+');
        if (international)
        {
            // «+44 (0)20 7946 0000»: the zero in brackets is the trunk prefix of the country, not dialled from abroad.
            text = text.Replace("(0)", string.Empty, StringComparison.Ordinal);
        }

        var digits = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsAsciiDigit(character))
                digits.Append(character);
        }

        string significant;
        if (international)
        {
            significant = digits.ToString();
        }
        else if (digits.Length >= 2 && digits[0] == '0' && digits[1] == '0')
        {
            significant = digits.ToString(2, digits.Length - 2);
        }
        else
        {
            // The six digits are the ones the person typed, before the calling code is put in front of them.
            if (digits.Length < MinPhoneDigits)
                return null;

            significant = CallingCodeDigits(defaultCallingCode) + digits;
        }

        if (significant.Length < MinPhoneDigits || significant.Length > MaxPhoneDigits || IsPlaceholder(significant))
            return null;

        return "+" + significant;
    }

    /// <summary>
    /// The name in the form two spellings of it share: lowercase, accents and punctuation removed, the words sorted, so <c>Rossi, Mario</c>,
    /// <c>mario rossi</c> and <c>Mário  ROSSI</c> are all <c>mario rossi</c>. An apostrophe joins (<c>D'Angelo</c> is <c>dangelo</c>), every other
    /// mark separates (<c>Anna-Maria</c> is <c>anna maria</c>). Empty when the name has no letter or digit.
    /// </summary>
    public static string NormalizeName(string? firstName, string? lastName)
    {
        var text = $"{firstName} {lastName}".Trim();
        if (text.Length == 0)
            return string.Empty;

        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (character is '\'' or '’' or '‘' or '`' or '´' or 'ʼ')
                continue;

            if (!char.IsLetterOrDigit(character))
            {
                builder.Append(' ');
                continue;
            }

            // The letters that have no accent to drop, and that the other spellings of the same name write with plain ones.
            switch (character)
            {
                case 'ß':
                    builder.Append("ss");
                    break;
                case 'æ':
                    builder.Append("ae");
                    break;
                case 'œ':
                    builder.Append("oe");
                    break;
                case 'ø':
                    builder.Append('o');
                    break;
                case 'ł':
                    builder.Append('l');
                    break;
                case 'đ':
                    builder.Append('d');
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        var words = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Array.Sort(words, StringComparer.Ordinal);
        return string.Join(' ', words);
    }

    /// <summary>The number of words of a name already normalized by <see cref="NormalizeName"/>.</summary>
    public static int WordCount(string normalizedName) =>
        normalizedName.Length == 0 ? 0 : normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>The digits of a calling code (<c>+39</c> gives <c>39</c>); those of the default for a value that is not one.</summary>
    private static string CallingCodeDigits(string? callingCode)
    {
        var code = callingCode?.Trim();
        return CrmOptions.IsCallingCode(code) ? code![1..] : CrmOptions.DefaultCallingCodeValue[1..];
    }

    /// <summary>
    /// True when what follows a calling code of one, two or three digits is a single digit repeated, at least <see cref="MinPhoneDigits"/> times.
    /// </summary>
    private static bool IsPlaceholder(string significantDigits)
    {
        for (var codeLength = 1; codeLength <= 3; codeLength++)
        {
            if (significantDigits.Length - codeLength < MinPhoneDigits)
                break;

            var national = significantDigits.AsSpan(codeLength);
            if (national.IndexOfAnyExcept(national[0]) < 0)
                return true;
        }

        return false;
    }
}
