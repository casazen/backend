using System.ComponentModel.DataAnnotations;
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

    public static OrgSettingsDto FromOrg(Org org) => new()
    {
        Id = org.Id,
        Name = org.Name,
        Slug = org.Slug,
        ContactEmail = org.ContactEmail,
        ContactEmailPublic = org.ContactEmailPublic,
    };
}

/// <summary>Body of <c>PUT /api/orgs/me/settings</c>. Error messages are keys of <c>Resources/SharedResources.resx</c>.</summary>
public class UpdateOrgSettingsDto
{
    [Required(ErrorMessage = "OrgNameRequired")]
    [MaxLength(200, ErrorMessage = "OrgNameTooLong")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Free text is accepted and sanitized into slug form server-side (<c>OrgSlugHelper</c>); only its result must be unique and not reserved.</summary>
    [Required(ErrorMessage = "OrgSlugRequired")]
    [MaxLength(100, ErrorMessage = "OrgSlugTooLong")]
    public string Slug { get; set; } = string.Empty;

    [Required(ErrorMessage = "OrgContactEmailRequired")]
    [EmailAddress(ErrorMessage = "InvalidEmail")]
    [MaxLength(255, ErrorMessage = "OrgContactEmailTooLong")]
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>Explicit opt-in to publish <see cref="ContactEmail"/> on the public booking site. Defaults to false when omitted.</summary>
    public bool ContactEmailPublic { get; set; }
}
