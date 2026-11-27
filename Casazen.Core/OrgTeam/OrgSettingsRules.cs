using Casazen.Core.Branding;
using Casazen.Core.Exceptions;

namespace Casazen.Core.OrgTeam;

public static class OrgSettingsRules
{
    public const string ContactPhoneInvalidCode = "org_contact_phone_invalid";
    public const string DefaultLanguageInvalidCode = "org_default_language_invalid";

    public const int ContactPhoneMaxLength = 20;

    public static string? NormalizeContactPhone(string? value)
    {
        try
        {
            return OrgBrandingRules.NormalizePublicPhone(value);
        }
        catch (DomainRuleException)
        {
            throw new DomainRuleException(ContactPhoneInvalidCode, "OrgContactPhoneInvalid");
        }
    }

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
