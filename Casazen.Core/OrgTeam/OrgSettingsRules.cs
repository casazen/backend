using Casazen.Core.Branding;
using Casazen.Core.Exceptions;

namespace Casazen.Core.OrgTeam;

/// <summary>
/// Validation and normalization of the settings of the account that are not about the public site (AM-08b-b): the phone number
/// of the org and the language of the emails it sends to its team. One place for the rules, as <see cref="OrgOperatorRules"/> is
/// for the operator data of the booking site.
/// </summary>
public static class OrgSettingsRules
{
    public const string ContactPhoneInvalidCode = "org_contact_phone_invalid";
    public const string DefaultLanguageInvalidCode = "org_default_language_invalid";

    /// <summary>Longest phone number stored (the column): <c>+</c> and up to 15 digits fit.</summary>
    public const int ContactPhoneMaxLength = 20;

    /// <summary>
    /// The phone of the account as stored, by the rule of the public phone of the site (<see cref="OrgBrandingRules.NormalizePublicPhone"/>):
    /// <c>+</c> or digits only, 6 to 15 digits, <c>null</c> when empty. It proves nothing about the number being reachable. Throws
    /// <see cref="DomainRuleException"/> (<see cref="ContactPhoneInvalidCode"/>) for anything else.
    /// </summary>
    public static string? NormalizeContactPhone(string? value)
    {
        try
        {
            return OrgBrandingRules.NormalizePublicPhone(value);
        }
        catch (DomainRuleException)
        {
            // The same shape, with the message of this field (the public phone's message talks about the booking site).
            throw new DomainRuleException(ContactPhoneInvalidCode, "OrgContactPhoneInvalid");
        }
    }

    /// <summary>
    /// The language of the account as stored: <c>it</c> or <c>en</c> (any case), the default when blank. Throws
    /// <see cref="DomainRuleException"/> (<see cref="DefaultLanguageInvalidCode"/>) for another language.
    /// </summary>
    public static string NormalizeDefaultLanguage(string? value)
    {
        var language = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(language))
            return OrgInvitationRules.DefaultLanguage;

        return OrgInvitationRules.Languages.Contains(language)
            ? language
            : throw new DomainRuleException(DefaultLanguageInvalidCode, "OrgDefaultLanguageInvalid");
    }
}
