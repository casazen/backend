using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Orgs;

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

    [MaxLength(500, ErrorMessage = "OrgMemberPropertyIdsTooMany")]
    public List<Guid>? PropertyIds { get; set; }
}

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

public class OrgMemberPropertyDto
{
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;

    public bool Granted { get; set; }

    public int PeopleWithAccess { get; set; }
}
