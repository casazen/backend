using System.Globalization;
using System.Net.Mail;
using System.Text;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

public static class CrmNormalizer
{
    public const int MinPhoneDigits = 6;

    public const int MaxPhoneDigits = 15;

    public const int MaxEmailLength = 255;

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
            if (digits.Length < MinPhoneDigits)
                return null;

            significant = CallingCodeDigits(defaultCallingCode) + digits;
        }

        if (significant.Length < MinPhoneDigits || significant.Length > MaxPhoneDigits || IsPlaceholder(significant))
            return null;

        return "+" + significant;
    }

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

    public static int WordCount(string normalizedName) =>
        normalizedName.Length == 0 ? 0 : normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string CallingCodeDigits(string? callingCode)
    {
        var code = callingCode?.Trim();
        return CrmOptions.IsCallingCode(code) ? code![1..] : CrmOptions.DefaultCallingCodeValue[1..];
    }

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
