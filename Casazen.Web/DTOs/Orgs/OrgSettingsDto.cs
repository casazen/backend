using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Branding;
using Casazen.Core.Entities;
using Casazen.Core.OrgTeam;
using Casazen.Web.DTOs.Users;

namespace Casazen.Web.DTOs.Orgs;

public class OrgSettingsDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>When true, <see cref="ContactEmail"/> is shown on the public booking site (<c>PublicOrgDto</c>). Off by default (GDPR opt-in).</summary>
    public bool ContactEmailPublic { get; set; }

    public string? LegalName { get; set; }

    public string? RegisteredOffice { get; set; }

    public string? ContactPhone { get; set; }

    public string DefaultLanguage { get; set; } = OrgInvitationRules.DefaultLanguage;

    public static OrgSettingsDto FromOrg(Org org) => new()
    {
        Id = org.Id,
        Name = org.Name,
        Slug = org.Slug,
        ContactEmail = org.ContactEmail,
        ContactEmailPublic = org.ContactEmailPublic,
        LegalName = org.LegalName,
        RegisteredOffice = org.RegisteredOffice,
        ContactPhone = org.ContactPhone,
        DefaultLanguage = org.DefaultLanguage,
    };
}

/// <summary>
/// Body of <c>PUT /api/orgs/me/settings</c>. Error messages are keys of <c>Resources/SharedResources.resx</c>.
/// </summary>
/// <remarks>
/// The name, the slug and the contact email are replaced as a whole, as they always were. The operator data of the booking-site
/// footer (DB-03b-a: <see cref="LegalName"/>, <see cref="RegisteredOffice"/>) and the settings of the account (AM-08b-b:
/// <see cref="ContactPhone"/>, <see cref="DefaultLanguage"/>) follow the rule of the public profile of
/// <c>PUT /api/orgs/me/branding</c> instead: a member that is not in the body keeps its stored value, so a client that does not
/// know it never erases it; <c>null</c> or a blank clears it (the language goes back to the default).
/// </remarks>
public class UpdateOrgSettingsDto
{
    [Required(ErrorMessage = "OrgNameRequired")]
    [MaxLength(200, ErrorMessage = "OrgNameTooLong")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "OrgSlugRequired")]
    [MaxLength(100, ErrorMessage = "OrgSlugTooLong")]
    public string Slug { get; set; } = string.Empty;

    [Required(ErrorMessage = "OrgContactEmailRequired")]
    [EmailAddress(ErrorMessage = "InvalidEmail")]
    [MaxLength(255, ErrorMessage = "OrgContactEmailTooLong")]
    public string ContactEmail { get; set; } = string.Empty;

    public bool ContactEmailPublic { get; set; }

    [MaxLength(OrgOperatorRules.LegalNameMaxLength, ErrorMessage = "OrgOperatorLegalNameTooLongInput")]
    public string? LegalName
    {
        get;
        set
        {
            field = value;
            LegalNameSent = true;
        }
    }

    [MaxLength(OrgOperatorRules.RegisteredOfficeMaxLength, ErrorMessage = "OrgOperatorRegisteredOfficeTooLongInput")]
    public string? RegisteredOffice
    {
        get;
        set
        {
            field = value;
            RegisteredOfficeSent = true;
        }
    }

    /// <summary>
    /// The phone number of the account (AM-08b-b): digits with an optional leading <c>+</c> and the usual separators, 6 to 15 digits
    /// (<see cref="OrgSettingsRules.NormalizeContactPhone"/>); stored without the separators and <b>never published</b>. Left out =
    /// unchanged; <c>null</c> or blank = none.
    /// </summary>
    [MaxLength(40, ErrorMessage = "OrgContactPhoneInvalid")]
    public string? ContactPhone
    {
        get;
        set
        {
            field = value;
            ContactPhoneSent = true;
        }
    }

    [MaxLength(8, ErrorMessage = "OrgDefaultLanguageInvalid")]
    public string? DefaultLanguage
    {
        get;
        set
        {
            field = value;
            DefaultLanguageSent = true;
        }
    }

    [JsonIgnore]
    public bool LegalNameSent { get; private set; }

    [JsonIgnore]
    public bool RegisteredOfficeSent { get; private set; }

    [JsonIgnore]
    public bool ContactPhoneSent { get; private set; }

    [JsonIgnore]
    public bool DefaultLanguageSent { get; private set; }
}

public class OrgSlugAvailabilityDto
{
    public string Slug { get; set; } = string.Empty;

    public bool Available { get; set; }

    public string? Code { get; set; }
}
