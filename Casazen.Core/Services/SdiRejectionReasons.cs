using System.Text.RegularExpressions;
using Casazen.Core.Documents;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The reasons the SDI gives a rejected invoice, in plain words (E17/F01/US02/T01, NF-86, NF-116): the map from the code of the notification of rejection («scarto») to
/// the key of its text in <c>SharedResources</c> (Italian default, English), and the words of the refusals of the channel itself (<see cref="SdiSubmissionErrors"/>). A
/// code that is not in the map is the generic text with the raw code in it, never a guess.
/// </summary>
/// <remarks>
/// <para><b>Source.</b> The 63 codes of the official list «Elenco dei controlli effettuati sul file fattura del Sistema di Interscambio», versione 2.0 (31/01/2025, valid from
/// 15/05/2026), Agenzia delle Entrate, fatturapa.gov.it, page «Documentazione Sistema d'Interscambio»; the file, its address and its SHA-256 are in
/// <c>docs/runbooks/fiscal-documents.md</c>. The map is the list, family by family: several codes share a text where the cause and what to do about it are the same.</para>
/// <para><b>Never personal data.</b> What an SDI notification says is kept only after <see cref="Clean"/> (no e-mail address, fiscal code or long number, at most
/// <see cref="FiscalDocument.SdiErrorDetailMaxLength"/> characters).</para>
/// </remarks>
public static partial class SdiRejectionReasons
{
    /// <summary>The key of the text of a code the map does not know: it says the raw code (<c>{0}</c>).</summary>
    public const string GenericKey = "SdiReasonGeneric";

    /// <summary>The key of the text of a rejection that came without any code.</summary>
    public const string NoCodeKey = "SdiReasonNoCode";

    /// <summary>The longest code of an SDI notification (five digits).</summary>
    public const int CodeLength = 5;

    private static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // The name, the size and the signature of the file (the channel signs the file and names it).
        ["00001"] = "SdiReasonFileName",
        ["00002"] = "SdiReasonFileNameDuplicate",
        ["00003"] = "SdiReasonFileSize",
        ["00100"] = "SdiReasonSignature",
        ["00101"] = "SdiReasonSignature",
        ["00102"] = "SdiReasonSignature",
        ["00103"] = "SdiReasonSignature",
        ["00104"] = "SdiReasonSignature",
        ["00105"] = "SdiReasonSignature",
        ["00107"] = "SdiReasonSignature",
        ["00106"] = "SdiReasonFileCorrupt",

        // The format of the file.
        ["00200"] = "SdiReasonFormat",
        ["00201"] = "SdiReasonFormat",

        // The content of the invoice: VAT rate and nature, dates, withholding, amounts, the kind of document.
        ["00400"] = "SdiReasonNatureMissing",
        ["00413"] = "SdiReasonNatureMissing",
        ["00429"] = "SdiReasonNatureMissing",
        ["00401"] = "SdiReasonNatureNotAllowed",
        ["00414"] = "SdiReasonNatureNotAllowed",
        ["00430"] = "SdiReasonNatureNotAllowed",
        ["00445"] = "SdiReasonNatureGeneric",
        ["00403"] = "SdiReasonDateFuture",
        ["00418"] = "SdiReasonDateBeforeRelated",
        ["00411"] = "SdiReasonWithholding",
        ["00415"] = "SdiReasonWithholding",
        ["00417"] = "SdiReasonRecipientIdMissing",
        ["00419"] = "SdiReasonVatSummary",
        ["00420"] = "SdiReasonVatSummary",
        ["00424"] = "SdiReasonVatSummary",
        ["00443"] = "SdiReasonVatSummary",
        ["00444"] = "SdiReasonVatSummary",
        ["00421"] = "SdiReasonAmounts",
        ["00422"] = "SdiReasonAmounts",
        ["00423"] = "SdiReasonAmounts",
        ["00425"] = "SdiReasonNumber",
        ["00427"] = "SdiReasonRecipientCode",
        ["00428"] = "SdiReasonFormatVersion",
        ["00437"] = "SdiReasonDiscount",
        ["00438"] = "SdiReasonDiscount",
        ["00471"] = "SdiReasonDocumentType",
        ["00472"] = "SdiReasonDocumentType",
        ["00473"] = "SdiReasonDocumentType",
        ["00474"] = "SdiReasonDocumentType",
        ["00475"] = "SdiReasonDocumentType",
        ["00476"] = "SdiReasonDocumentType",
        ["00477"] = "SdiReasonIntentDeclaration",

        // Who is who: the fiscal ids of the parties and the recipient code.
        ["00300"] = "SdiReasonTransmitterId",
        ["00301"] = "SdiReasonIssuerId",
        ["00302"] = "SdiReasonIssuerId",
        ["00320"] = "SdiReasonIssuerId",
        ["00321"] = "SdiReasonIssuerId",
        ["00322"] = "SdiReasonIssuerId",
        ["00323"] = "SdiReasonIssuerId",
        ["00303"] = "SdiReasonRepresentativeId",
        ["00304"] = "SdiReasonRepresentativeId",
        ["00305"] = "SdiReasonRecipientId",
        ["00306"] = "SdiReasonRecipientId",
        ["00324"] = "SdiReasonRecipientId",
        ["00311"] = "SdiReasonRecipientCode",
        ["00312"] = "SdiReasonRecipientCode",
        ["00398"] = "SdiReasonPublicBody",
        ["00399"] = "SdiReasonPublicBody",

        // The invoice was sent before.
        ["00404"] = "SdiReasonDuplicate",
        ["00409"] = "SdiReasonDuplicate",

        // The refusals of the channel, before the SDI saw the file.
        [SdiSubmissionErrors.IssuerPending] = "SdiErrorIssuerPending",
        [SdiSubmissionErrors.ProviderRefused] = "SdiErrorProviderRefused",
        [SdiSubmissionErrors.ProviderError] = "SdiErrorProviderError",
        [SdiSubmissionErrors.NotSupported] = "SdiErrorNotSupported",
    };

    /// <summary>The codes of the map: the 63 of the official list and the four of the channel.</summary>
    public static IReadOnlyCollection<string> KnownCodes => Keys.Keys.ToList();

    /// <summary>The codes of the notification of rejection of the official list.</summary>
    public static IReadOnlyCollection<string> SdiCodes => Keys.Keys.Where(IsSdiCode).ToList();

    /// <summary>The code as the map spells it: trimmed, a number of one to five digits written with five (<c>300</c> is <c>00300</c>); a code of the channel in lower case; <c>null</c> for nothing.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var trimmed = code.Trim();
        if (trimmed.Length <= CodeLength && trimmed.All(char.IsAsciiDigit))
            return trimmed.PadLeft(CodeLength, '0');

        return trimmed.ToLowerInvariant();
    }

    /// <summary>True when the map has a text of its own for <paramref name="code"/> (an SDI code of the list, or a code of the channel).</summary>
    public static bool IsKnown(string? code) => Normalize(code) is { } normalized && Keys.ContainsKey(normalized);

    /// <summary>The key in <c>SharedResources</c> of the words of <paramref name="code"/>: its own, else <see cref="GenericKey"/> (or <see cref="NoCodeKey"/> without a code).</summary>
    public static string KeyOf(string? code)
    {
        var normalized = Normalize(code);
        if (normalized is null)
            return NoCodeKey;

        return Keys.TryGetValue(normalized, out var key) ? key : GenericKey;
    }

    /// <summary>The reason of <paramref name="code"/> in plain words, in <paramref name="language"/>: the generic text carries the raw code of a code the map does not know.</summary>
    public static string Describe(string? code, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var normalized = Normalize(code);
        var key = KeyOf(normalized);
        return key == GenericKey ? texts.Get(key, language, Raw(normalized)) : texts.Get(key, language);
    }

    /// <summary>
    /// What an SDI notification or an answer of the channel says, ready to be kept: control characters and runs of spaces collapsed, an e-mail address, a fiscal code
    /// of a person and any number of nine or more digits masked (<c>***</c>), at most <paramref name="maxLength"/> characters. <c>null</c> for nothing.
    /// </summary>
    public static string? Clean(string? text, int maxLength = FiscalDocument.SdiErrorDetailMaxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var collapsed = Spaces().Replace(Controls().Replace(text, " "), " ").Trim();
        var masked = LongNumbers().Replace(FiscalCodes().Replace(Emails().Replace(collapsed, "***"), "***"), "***");
        if (masked.Length == 0)
            return null;

        return masked.Length <= maxLength ? masked : masked[..maxLength].TrimEnd();
    }

    /// <summary>True for a code of the notification of rejection of the official list (five digits).</summary>
    private static bool IsSdiCode(string code) => code.Length == CodeLength && code.All(char.IsAsciiDigit);

    private static string Raw(string? normalized) => normalized ?? "-";

    [GeneratedRegex(@"[\u0000-\u001F\u007F]")]
    private static partial Regex Controls();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Emails();

    [GeneratedRegex(@"\b[A-Za-z]{6}\d{2}[A-Za-z]\d{2}[A-Za-z]\d{3}[A-Za-z]\b")]
    private static partial Regex FiscalCodes();

    [GeneratedRegex(@"\d{9,}")]
    private static partial Regex LongNumbers();
}
