using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Casazen.Core.SiteDocuments;

namespace Casazen.Core.Entities;

/// <summary>
/// One published version of an operator document of the public booking site (BK-14, A3-21): the host's privacy notice
/// or booking terms, as text written in CasaZen or as a link to a document hosted elsewhere. Versions are immutable
/// and numbered per org and kind from 1: publishing again adds a version, nothing is edited in place, so the text a
/// guest saw on a given date can always be told. The current version of a kind is the highest one; it is shown unless
/// <see cref="WithdrawnAt"/> is set, in which case the site says the operator has not published the document.
/// </summary>
[Table("OrgSiteDocuments")]
public class OrgSiteDocument : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Org Org { get; set; } = null!;

    public OrgSiteDocumentKind Kind { get; set; }

    public int Version { get; set; }

    public OrgSiteDocumentSource Source { get; set; }

    public string? Content { get; set; }

    public string? ContentHtml { get; set; }

    [MaxLength(OrgSiteDocumentRules.ExternalUrlMaxLength)]
    public string? ExternalUrl { get; set; }

    public DateTime PublishedAt { get; set; }

    [MaxLength(255)]
    public string? PublishedByUserId { get; set; }

    public DateTime? WithdrawnAt { get; set; }
}
