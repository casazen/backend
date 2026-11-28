using System.Net.Mail;

namespace Casazen.Web.DTOs.Billing;

/// <summary>
/// Shape checks of the billing profile (PL-13). They only reject values that cannot be right (wrong characters, too long
/// for the column); the official formats of the SDI recipient code and of the codice fiscale are not encoded here
/// (D14: no normative data without a verified source, see docs/runbooks/billing-tax.md). The VAT id is verified by
/// Stripe (VIES) on the one entered at checkout.
/// </summary>
public static class BillingProfileValidation
{
    public static string? NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = new string(value.Where(c => !char.IsWhiteSpace(c) && c is not ('.' or '-')).ToArray())
            .ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>Same rule as the web app: letters and digits, 4-20 characters once normalized. Empty = no VAT id.</summary>
    public static bool IsValidVatId(string? vatId)
    {
        var normalized = NormalizeCode(vatId);
        return normalized is null || (normalized.Length is >= 4 and <= 20 && IsAsciiAlphanumeric(normalized));
    }

    public static bool IsValidSdiRecipientCode(string? normalized) =>
        string.IsNullOrEmpty(normalized) || (normalized.Length <= 7 && IsAsciiAlphanumeric(normalized));

    public static bool IsValidFiscalCode(string? normalized) =>
        string.IsNullOrEmpty(normalized) || (normalized.Length <= 16 && IsAsciiAlphanumeric(normalized));

    public static bool IsValidPecEmail(string? pec)
    {
        if (string.IsNullOrEmpty(pec))
            return true;

        if (pec.Length > 255 || !MailAddress.TryCreate(pec, out var address))
            return false;

        return string.Equals(address.Address, pec, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAsciiAlphanumeric(string value) => value.All(char.IsAsciiLetterOrDigit);
}
