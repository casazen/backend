using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Branding;
using Casazen.Core.Entities;
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

    public static OrgSettingsDto FromOrg(Org org) => new()
    {
        Id = org.Id,
        Name = org.Name,
        Slug = org.Slug,
        ContactEmail = org.ContactEmail,
        ContactEmailPublic = org.ContactEmailPublic,
        LegalName = org.LegalName,
        RegisteredOffice = org.RegisteredOffice,
    };
}

/// <summary>
/// Body of <c>PUT /api/orgs/me/settings</c>. Error messages are keys of <c>Resources/SharedResources.resx</c>.
/// </summary>
/// <remarks>
/// The name, the slug and the contact email are replaced as a whole, as they always were. The operator data of the booking-site
/// footer (DB-03b-a: <see cref="LegalName"/>, <see cref="RegisteredOffice"/>) follows the rule of the public profile of
/// <c>PUT /api/orgs/me/branding</c> instead: a member that is not in the body keeps its stored value, so a client that does not
/// know it never erases it; <c>null</c> or a blank clears it.
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

    /// <summary>True when the body carries <see cref="LegalName"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool LegalNameSent { get; private set; }

    /// <summary>True when the body carries <see cref="RegisteredOffice"/>, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool RegisteredOfficeSent { get; private set; }
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
