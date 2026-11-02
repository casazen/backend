using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Branding;
using Casazen.Core.Entities;
using Casazen.Core.OrgTeam;
using Casazen.Web.DTOs.Users;

namespace Casazen.Web.DTOs.Orgs;

/// <summary>
/// The caller org's editable identity (A1-22, A1-23): <c>GET/PUT /api/orgs/me/settings</c>. Unlike
/// <see cref="OrgSummaryDto"/> (shown to every member) this carries the contact email and its publication
/// opt-in, so it is only ever returned to the org's billing/settings administrator (policy <c>OrgBillingAdmin</c>).
/// </summary>
public class OrgSettingsDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>When true, <see cref="ContactEmail"/> is shown on the public booking site (<c>PublicOrgDto</c>). Off by default (GDPR opt-in).</summary>
    public bool ContactEmailPublic { get; set; }

    /// <summary>The legal name of the operator that the booking site shows in its footer (DB-03b-a), or null while not entered.</summary>
    public string? LegalName { get; set; }

    /// <summary>The registered office ("sede legale") that the booking site shows in its footer (DB-03b-a), or null while not entered.</summary>
    public string? RegisteredOffice { get; set; }

    /// <summary>The phone number of the account (AM-08b-b), or null while not entered. Never published: only the org's administrators read it.</summary>
    public string? ContactPhone { get; set; }

    /// <summary>The language of the account (AM-08b-b): <c>it</c> or <c>en</c>. The language of the emails to the team when nobody chooses another.</summary>
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

    /// <summary>
    /// Free text is accepted and sanitized into slug form server-side (<c>OrgSlugHelper</c>: 3-63 characters, not
    /// reserved, unique). The current slug sent back unchanged is kept as is, even a legacy one.
    /// </summary>
    [Required(ErrorMessage = "OrgSlugRequired")]
    [MaxLength(100, ErrorMessage = "OrgSlugTooLong")]
    public string Slug { get; set; } = string.Empty;

    [Required(ErrorMessage = "OrgContactEmailRequired")]
    [EmailAddress(ErrorMessage = "InvalidEmail")]
    [MaxLength(255, ErrorMessage = "OrgContactEmailTooLong")]
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>Explicit opt-in to publish <see cref="ContactEmail"/> on the public booking site. Defaults to false when omitted.</summary>
    public bool ContactEmailPublic { get; set; }

    /// <summary>
    /// The legal name of the operator (DB-03b-a), plain text, at most <see cref="OrgOperatorRules.LegalNameMaxLength"/> characters
    /// once whitespace is collapsed. Whatever is saved here is shown in the footer of the booking site. Left out = unchanged;
    /// <c>null</c> or blank = none.
    /// </summary>
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

    /// <summary>
    /// The registered office ("sede legale") of the operator (DB-03b-a), one line of plain text, at most
    /// <see cref="OrgOperatorRules.RegisteredOfficeMaxLength"/> characters once whitespace is collapsed. Left out = unchanged;
    /// <c>null</c> or blank = none.
    /// </summary>
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

    /// <summary>
    /// The language of the account (AM-08b-b): <c>it</c> or <c>en</c> (any case); the language of the emails the org sends to its team
    /// when nobody chooses another. Left out = unchanged; <c>null</c> or blank = Italian, the default.
    /// </summary>
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

    /// <summary>True when the body carries <see cref="LegalName"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool LegalNameSent { get; private set; }

    /// <summary>True when the body carries <see cref="RegisteredOffice"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool RegisteredOfficeSent { get; private set; }

    /// <summary>True when the body carries <see cref="ContactPhone"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool ContactPhoneSent { get; private set; }

    /// <summary>True when the body carries <see cref="DefaultLanguage"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool DefaultLanguageSent { get; private set; }
}

/// <summary>Result of <c>GET /api/orgs/me/settings/slug-availability</c> (A1-23).</summary>
public class OrgSlugAvailabilityDto
{
    /// <summary>The slug as it would be saved (sanitized).</summary>
    public string Slug { get; set; } = string.Empty;

    public bool Available { get; set; }

    /// <summary>Why it is not available: <c>org_slug_invalid</c>, <c>org_slug_reserved</c> or <c>org_slug_taken</c>; null when available.</summary>
    public string? Code { get; set; }
}
