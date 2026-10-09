using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A previous public slug of an org (PL-04, A1-23). When the host changes <see cref="Org.Slug"/> the old value is kept
/// here: links already shared with guests (<c>/book/{slug}</c>, booking, checkout and confirmation emails) keep
/// resolving to the same org, and no other org can take the old value and receive its guests.
/// </summary>
[Table("OrgSlugAliases")]
public class OrgSlugAlias : ITenantOwned
{
    /// <summary>The previous slug; unique across aliases and never equal to another org's current slug.</summary>
    [Key, MaxLength(100)]
    public string Slug { get; set; } = string.Empty;

    public Guid OrgId { get; set; }

    public Org Org { get; set; } = null!;

    /// <summary>When the org stopped using this slug (UTC).</summary>
    public DateTime CreatedAt { get; set; }
}
