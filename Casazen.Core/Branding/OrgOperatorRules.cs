using System.Text.RegularExpressions;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Branding;

/// <summary>
/// Validation and normalization of the operator data of the public booking site (DB-03b-a): the legal name and the registered
/// office that the footer of the site shows next to the VAT number. Plain text, trimmed, any run of whitespace collapsed into one
/// space, <c>null</c> when empty: an empty line is never published. One place for the rules; the console mirrors the limits as the
/// <c>maxLength</c> of the two fields of the organization settings.
/// </summary>
public static partial class OrgOperatorRules
{
    public const string LegalNameTooLongCode = "org_operator_legal_name_too_long";
    public const string RegisteredOfficeTooLongCode = "org_operator_registered_office_too_long";

    /// <summary>Longest legal name ("ragione sociale"), after whitespace is collapsed; the same bound as the name of the org.</summary>
    public const int LegalNameMaxLength = 200;

    /// <summary>Longest registered office ("sede legale"): street, postal code, town and province in one line.</summary>
    public const int RegisteredOfficeMaxLength = 300;

    /// <summary>
    /// The legal name as stored: trimmed, whitespace collapsed, <c>null</c> when empty. Throws <see cref="DomainRuleException"/>
    /// (<see cref="LegalNameTooLongCode"/>) above <see cref="LegalNameMaxLength"/> characters.
    /// </summary>
    public static string? NormalizeLegalName(string? value)
    {
        var collapsed = Collapse(value);
        return collapsed is null || collapsed.Length <= LegalNameMaxLength
            ? collapsed
            : throw new DomainRuleException(LegalNameTooLongCode, "OrgOperatorLegalNameTooLong", LegalNameMaxLength);
    }

    /// <summary>
    /// The registered office as stored, by the same rule as <see cref="NormalizeLegalName"/>. Throws
    /// <see cref="DomainRuleException"/> (<see cref="RegisteredOfficeTooLongCode"/>) above <see cref="RegisteredOfficeMaxLength"/>
    /// characters.
    /// </summary>
    public static string? NormalizeRegisteredOffice(string? value)
    {
        var collapsed = Collapse(value);
        return collapsed is null || collapsed.Length <= RegisteredOfficeMaxLength
            ? collapsed
            : throw new DomainRuleException(
                RegisteredOfficeTooLongCode, "OrgOperatorRegisteredOfficeTooLong", RegisteredOfficeMaxLength);
    }

    private static string? Collapse(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Whitespace().Replace(value.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
