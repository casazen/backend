using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.BankStatements;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// How the facts of an owner are written and checked (E10/F02/US02/T01, NF-109): the one place that knows the shape of a fiscal identifier, a VAT
/// number, an IBAN, a BIC, a country and a currency of the registry. The owner import normalizes with it before it matches or stores, and the
/// readiness judges with it what is stored, so a value the import accepts is never one the readiness would call wrong. It only reads the shape:
/// it proves nothing about who issued the identifier. The fiscal code and the VAT number of Italy are checked by their check character
/// (<see cref="ItalianFiscalCode"/>, <see cref="InvoiceDataFormats"/>), the IBAN by its length and checksum (<see cref="BankValues"/>).
/// </summary>
public static partial class OwnerFieldRules
{
    public const string Italy = "IT";

    private static readonly HashSet<string> SepaCountries = new(StringComparer.Ordinal)
    {
        "AD", "AT", "BE", "BG", "CH", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FR", "GB", "GR", "HR", "HU", "IE", "IS", "IT", "LI", "LT", "LU",
        "LV", "MC", "MT", "NL", "NO", "PL", "PT", "RO", "SE", "SI", "SK", "SM", "VA",
    };

    private static readonly Dictionary<string, string> CountryNames = new(StringComparer.Ordinal)
    {
        ["italia"] = "IT",
        ["italy"] = "IT",
        ["svizzera"] = "CH",
        ["switzerland"] = "CH",
        ["schweiz"] = "CH",
        ["suisse"] = "CH",
        ["svizra"] = "CH",
        ["francia"] = "FR",
        ["france"] = "FR",
        ["germania"] = "DE",
        ["germany"] = "DE",
        ["deutschland"] = "DE",
        ["spagna"] = "ES",
        ["spain"] = "ES",
        ["austria"] = "AT",
        ["sanmarino"] = "SM",
        ["regnounito"] = "GB",
        ["unitedkingdom"] = "GB",
    };

    [GeneratedRegex("^[A-Z]{4}[A-Z]{2}[A-Z0-9]{2}([A-Z0-9]{3})?$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex BicShape();

    [GeneratedRegex("^[A-Z0-9]{4,40}$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex ForeignTaxIdShape();

    [GeneratedRegex("^[A-Z0-9]{4,30}$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex ForeignVatShape();

    public static string Key(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
        var key = new System.Text.StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
                key.Append(char.ToLowerInvariant(ch));
        }

        return key.ToString();
    }

    public static bool TryParseKind(string? value, out OwnerEntityKind kind)
    {
        switch (Key(value))
        {
            case "person" or "naturalperson" or "individual" or "natural" or "persona" or "personafisica" or "fisica" or "pf" or "privato" or "private":
                kind = OwnerEntityKind.NaturalPerson;
                return true;
            case "company" or "legalentity" or "legal" or "entity" or "business" or "societa" or "personagiuridica" or "giuridica" or "pg" or "azienda"
                or "ente" or "impresa" or "associazione":
                kind = OwnerEntityKind.LegalEntity;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public static bool TryNormalizeCountry(string? value, out string country)
    {
        country = string.Empty;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        if (text.Length == 2 && text.All(char.IsAsciiLetter))
        {
            country = text.ToUpperInvariant();
            return true;
        }

        if (CountryNames.TryGetValue(Key(text), out var named))
        {
            country = named;
            return true;
        }

        return false;
    }

    public static bool TryNormalizeCurrency(string? value, out string currency)
    {
        currency = string.Empty;
        var text = value?.Trim();
        if (text is null || text.Length != 3 || !text.All(char.IsAsciiLetter))
            return false;

        currency = text.ToUpperInvariant();
        return true;
    }

    public static string NormalizeFiscalCode(string? value) => ItalianFiscalCode.Normalize(value);

    /// <summary>
    /// The VAT number in the form compared and stored: upper case, without spaces, dots, dashes and slashes; an Italian number is stored as its
    /// eleven digits, without the <c>IT</c> prefix, so <c>IT 01234567897</c> and <c>01234567897</c> are one number.
    /// </summary>
    public static string NormalizeVat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var compact = new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return compact.Length == 13 && compact.StartsWith("IT", StringComparison.Ordinal) && compact[2..].All(char.IsAsciiDigit) ? compact[2..] : compact;
    }

    public static string NormalizeIban(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : new string(value.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());

    public static string NormalizeBic(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : new string(value.Where(c => !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray());

    public static bool IsValidFiscalId(string normalized, string? residence) =>
        IsItalian(residence) ? ItalianFiscalCode.IsValid(normalized) : ForeignTaxIdShape().IsMatch(normalized);

    /// <summary>True when <paramref name="normalized"/> (<see cref="NormalizeVat"/>) is a VAT number for an owner resident in <paramref name="residence"/>.</summary>
    public static bool IsValidVat(string normalized, string? residence) =>
        IsItalian(residence) ? InvoiceDataFormats.IsValidVatNumber(normalized) : ForeignVatShape().IsMatch(normalized);

    public static bool IsValidIban(string normalized) => BankValues.IsValidIban(normalized);

    public static bool IsValidBic(string normalized) => BicShape().IsMatch(normalized);

    public static bool IsSepaIban(string normalizedIban) =>
        normalizedIban.Length >= 2 && SepaCountries.Contains(normalizedIban[..2]);

    public static bool IsValidEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 255 && new EmailAddressAttribute().IsValid(value.Trim());

    private static bool IsItalian(string? residence) => string.IsNullOrEmpty(residence) || string.Equals(residence, Italy, StringComparison.Ordinal);

    public static string DisplayName(PropertyOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!string.IsNullOrWhiteSpace(owner.LegalName))
            return owner.LegalName.Trim();

        return $"{owner.FirstName} {owner.LastName}".Trim();
    }

    public static (string FirstName, string LastName) SplitName(string legalName, OwnerEntityKind kind)
    {
        var name = legalName.Trim();
        if (kind == OwnerEntityKind.LegalEntity)
            return (string.Empty, Clip(name, 100));

        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (Clip(parts[0], 100), Clip(parts[1], 100)) : (string.Empty, Clip(name, 100));
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
