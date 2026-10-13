namespace Casazen.Core.Utilities;

/// <summary>
/// Masks personal data returned by the API (LT-11, A7-17): screens show only what the host needs to recognise a
/// person, never the full identifier. The full values stay in the database and reach only the documents that need
/// them (contract PDF, RLI export).
/// </summary>
public static class PersonalDataMasking
{
    private const char MaskChar = '*';
    private const int FiscalCodeVisibleChars = 4;

    /// <summary>
    /// Keeps the last four characters of a codice fiscale (or partita IVA): <c>RSSMRA80A01H501Z</c> becomes
    /// <c>************501Z</c>. Values of four characters or less are hidden completely.
    /// </summary>
    public static string MaskFiscalCode(string? fiscalCode)
    {
        if (string.IsNullOrWhiteSpace(fiscalCode))
            return string.Empty;

        var value = fiscalCode.Trim();
        if (value.Length <= FiscalCodeVisibleChars)
            return new string(MaskChar, FiscalCodeVisibleChars);

        return new string(MaskChar, value.Length - FiscalCodeVisibleChars) + value[^FiscalCodeVisibleChars..];
    }

    /// <summary>
    /// First character of the local part plus the domain: <c>mario.rossi@example.it</c> becomes
    /// <c>m***@example.it</c>. A value that is not an address is hidden completely.
    /// </summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return string.Empty;

        var value = email.Trim();
        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1)
            return new string(MaskChar, 3);

        return $"{value[0]}{new string(MaskChar, 3)}{value[at..]}";
    }
}
