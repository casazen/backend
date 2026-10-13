using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Orgs;

// The properties a member of the org reaches (AM-03). Enums travel as member names (the API's single enum converter); every
// error message is a key of Resources/SharedResources.resx.

/// <summary>Body of <c>PUT /api/orgs/me/members/{id}/properties</c>.</summary>
public class SetOrgMemberPropertiesRequest
{
    /// <summary>
    /// <c>All</c>: every property of the org, the ones added later too (the list below is ignored). <c>Selected</c>
    /// («Solo alcuni»): exactly the properties of <see cref="PropertyIds"/>; none means the person sees nothing. Only a
    /// collaborator can be <c>Selected</c> (422 <c>org_member_scope_not_supported</c> otherwise).
    /// </summary>
    [Required(ErrorMessage = "OrgMemberPropertyScopeRequired")]
    [EnumDataType(typeof(PropertyScope), ErrorMessage = "OrgMemberPropertyScopeRequired")]
    public PropertyScope? PropertyScope { get; set; }

    /// <summary>The properties the person reaches with <c>Selected</c>: ids of properties of the org, no more than 500.</summary>
    [MaxLength(500, ErrorMessage = "OrgMemberPropertyIdsTooMany")]
    public List<Guid>? PropertyIds { get; set; }
}

/// <summary>
/// The access of a member to the properties of the org: for every property whether it reaches it and how many people do
/// (<c>peopleWithAccess</c>, «chi può accedere»: the active members who reach it). <c>scopeSupported</c> is false for a member
/// who is not a collaborator: it reaches every property and cannot be limited.
/// </summary>
public class OrgMemberPropertiesDto
{
    public Guid MemberId { get; set; }
    public OrgRole Role { get; set; }
    public PropertyScope PropertyScope { get; set; }
    public bool ScopeSupported { get; set; }
    public IReadOnlyList<OrgMemberPropertyDto> Properties { get; set; } = [];

    public static OrgMemberPropertiesDto From(OrgMemberPropertyAccessView view) => new()
    {
        MemberId = view.MemberId,
        Role = view.Role,
        PropertyScope = view.PropertyScope,
        ScopeSupported = view.ScopeSupported,
        Properties = view.Properties
            .Select(p => new OrgMemberPropertyDto
            {
                PropertyId = p.PropertyId,
                Name = p.Name,
                City = p.City,
                Granted = p.Granted,
                PeopleWithAccess = p.PeopleWithAccess,
            })
            .ToList(),
    };
}

/// <summary>One property of the org in the access page of a member.</summary>
public class OrgMemberPropertyDto
{
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;

    /// <summary>The member reaches this property.</summary>
    public bool Granted { get; set; }

    /// <summary>How many active people of the org reach this property (the owner, the administrators… and the collaborators who were given it).</summary>
    public int PeopleWithAccess { get; set; }
}
