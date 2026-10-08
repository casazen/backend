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

    /// <summary>1, 2, 3... per org and kind; unique with them.</summary>
    public int Version { get; set; }

    public OrgSiteDocumentSource Source { get; set; }

    /// <summary>The text as the operator wrote it (normalized, <see cref="OrgSiteDocumentSource.Text"/> only).</summary>
    public string? Content { get; set; }

    /// <summary>Sanitized HTML built from <see cref="Content"/> when the version was published.</summary>
    public string? ContentHtml { get; set; }

    /// <summary>Absolute https address of the document (<see cref="OrgSiteDocumentSource.ExternalUrl"/> only).</summary>
    [MaxLength(OrgSiteDocumentRules.ExternalUrlMaxLength)]
    public string? ExternalUrl { get; set; }

    /// <summary>When the version was published (UTC).</summary>
    public DateTime PublishedAt { get; set; }

    /// <summary>Auth0 subject of the administrator who published it.</summary>
    [MaxLength(255)]
    public string? PublishedByUserId { get; set; }

    /// <summary>When the operator withdrew the document (UTC); null while it is shown. Only the current version can be withdrawn.</summary>
    public DateTime? WithdrawnAt { get; set; }
}
