using System.Globalization;
using Casazen.Core.BankStatements;

namespace Casazen.Core.Regulatory;

/// <summary>
/// The Swiss enterprise identification number (UID) used as the VAT number of a registered Swiss issuer (MWSTG art. 26: «CHE-123.456.789 IVA»).
/// </summary>
/// <remarks>
/// The check digit is the one of the standard eCH-0097 «Datenstandard Unternehmensidentifikation» (v5.2.0, § 2.4.2, published by eCH for the Federal
/// Statistical Office): the eight digits are multiplied by 5, 4, 3, 2, 7, 6, 5, 4, the products are added, the rest of the division by 11 is taken from
/// 11 and gives the check digit; a rest of 0 gives 0, and a result of 10 is a number that is never assigned (invalid). Example of the standard:
/// <c>CHE-109.322.551</c>.
/// </remarks>
public static class SwissUid
{
    /// <summary>The prefix of the UID of an enterprise.</summary>
    public const string Prefix = "CHE";

    private static readonly int[] Weights = [5, 4, 3, 2, 7, 6, 5, 4];

    /// <summary>The suffixes of the VAT register a number may carry when it is typed (they are not stored).</summary>
    private static readonly string[] VatSuffixes = ["MWST", "TVA", "IVA", "VAT"];

    /// <summary>
    /// The UID in its stored form <c>CHE-123.456.789</c>, or <c>null</c> when <paramref name="value"/> is not a UID with a right check digit. Spaces, dots,
    /// hyphens and a VAT suffix (<c>MWST</c>, <c>TVA</c>, <c>IVA</c>, <c>VAT</c>) are accepted on input.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var compact = new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        foreach (var suffix in VatSuffixes)
        {
            if (compact.EndsWith(suffix, StringComparison.Ordinal))
            {
                compact = compact[..^suffix.Length];
                break;
            }
        }

        if (compact.Length != Prefix.Length + 9 || !compact.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        var digits = compact[Prefix.Length..];
        if (!digits.All(char.IsAsciiDigit) || CheckDigit(digits[..8]) is not { } check || check != digits[8] - '0')
            return null;

        return $"{Prefix}-{digits[..3]}.{digits[3..6]}.{digits[6..]}";
    }

    /// <summary>True when <paramref name="value"/> is a UID with a right check digit (see <see cref="Normalize"/>).</summary>
    public static bool IsValid(string? value) => Normalize(value) is not null;

    /// <summary>The check digit of the eight digits of a UID, or <c>null</c> when the computation gives 10 (a number never assigned).</summary>
    public static int? CheckDigit(string eightDigits)
    {
        ArgumentNullException.ThrowIfNull(eightDigits);
        if (eightDigits.Length != Weights.Length || !eightDigits.All(char.IsAsciiDigit))
            throw new ArgumentException("Eight digits are expected.", nameof(eightDigits));

        var sum = 0;
        for (var i = 0; i < Weights.Length; i++)
            sum += (eightDigits[i] - '0') * Weights[i];

        var rest = sum % 11;
        var check = rest == 0 ? 0 : 11 - rest;
        return check == 10 ? null : check;
    }
}

/// <summary>The bank accounts of an issuer: an IBAN of any country, and the QR-IBAN of the Swiss QR-bill.</summary>
public static class IssuerBankAccounts
{
    /// <summary>The lowest institution id of a QR-IBAN (positions 5 to 9 of a Swiss or Liechtenstein IBAN).</summary>
    public const int QrInstitutionIdFrom = 30000;

    /// <summary>The highest institution id of a QR-IBAN.</summary>
    public const int QrInstitutionIdTo = 31999;

    /// <summary>
    /// The IBAN in its stored form (upper case, no spaces), or <c>null</c> when it has not the length and the mod-97 check digits of an IBAN
    /// (<see cref="BankValues.IsValidIban"/>).
    /// </summary>
    public static string? NormalizeIban(string? value) => BankValues.NormalizeIban(value);

    /// <summary>True when <paramref name="normalizedIban"/> is a QR-IBAN: an IBAN of CH or LI whose institution id is 30000 to 31999.</summary>
    public static bool IsQrIban(string normalizedIban)
    {
        ArgumentNullException.ThrowIfNull(normalizedIban);
        if (normalizedIban.Length < 9 || !(normalizedIban.StartsWith("CH", StringComparison.Ordinal) || normalizedIban.StartsWith("LI", StringComparison.Ordinal)))
            return false;

        return int.TryParse(normalizedIban.AsSpan(4, 5), NumberStyles.None, CultureInfo.InvariantCulture, out var institution)
               && institution is >= QrInstitutionIdFrom and <= QrInstitutionIdTo;
    }

    /// <summary>The IBAN with only its country, its check digits and its last four characters visible (<c>CH93…2957</c>); <c>null</c> for none.</summary>
    public static string? Mask(string? normalizedIban)
    {
        if (string.IsNullOrWhiteSpace(normalizedIban))
            return null;

        return normalizedIban.Length <= 8 ? new string('*', normalizedIban.Length) : $"{normalizedIban[..4]}…{normalizedIban[^4..]}";
    }
}
