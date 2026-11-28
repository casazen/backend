using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("OrgSlugAliases")]
public class OrgSlugAlias : ITenantOwned
{
    [Key, MaxLength(100)]
    public string Slug { get; set; } = string.Empty;

    public Guid OrgId { get; set; }

    public Org Org { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
