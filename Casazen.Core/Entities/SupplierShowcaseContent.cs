using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

/// <summary>
/// The content of a supplier's public showcase (SP-13a, <c>api/supplier/showcase/content</c>): the <b>draft</b> the supplier is
/// writing, the <b>published</b> version the customers read, and the version published before it (for "Annulla"). One row per
/// supplier, created by the first save of the draft; a supplier that never saved anything has no row and keeps the plain page of
/// SU-13 (built from its profile).
/// </summary>
/// <remarks>
/// <para><b>Format.</b> The three JSON columns hold a <see cref="ShowcaseContentDocument"/> (<see cref="SchemaVersion"/> 1) in the
/// camel-case shape of the API. Plain text only, no contact data: phone, e-mail, WhatsApp and domain are not part of the content
/// (interview Q4/Q7, decision D11).</para>
/// <para><b>Tenancy.</b> Keyed by the supplier org (<see cref="OrgId"/> = <see cref="SupplierProfile.OrgId"/>), like
/// <see cref="SupplierProfile"/> and <see cref="SupplierSettings"/>: it is <b>not</b> <c>ITenantOwned</c> (a supplier acts as
/// <c>User.SupplierOrgId</c> and a supplier-only account has no <c>User.OrgId</c>). It is in the allow-list of
/// <c>TenantQueryFilterArchitectureTests</c> and every read and write filters by <see cref="OrgId"/> explicitly
/// (<c>SupplierShowcaseContentService</c> and the supplier repair only: <c>SupplierShowcaseContentTenancyTests</c>).</para>
/// <para><b>Concurrency.</b> <see cref="Version"/> is PostgreSQL's <c>xmin</c>: a save must carry the version the client read,
/// otherwise it is refused with 409. Every change also takes the supplier's content lock.</para>
/// </remarks>
[Table("SupplierShowcaseContents")]
public class SupplierShowcaseContent
{
    [Key]
    public Guid OrgId { get; set; }

    public int SchemaVersion { get; set; } = ShowcaseContentLimits.SchemaVersion;

    [Column(TypeName = "jsonb")]
    public string DraftJson { get; set; } = "{}";

    [Column(TypeName = "jsonb")]
    public string? PublishedJson { get; set; }

    [Column(TypeName = "jsonb")]
    public string? PreviousPublishedJson { get; set; }

    public DateTime? PreviousPublishedAt { get; set; }

    public DateTime? UndoUntil { get; set; }

    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PublishedAt { get; set; }

    public DateTime? PausedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token: mapped to PostgreSQL's <c>xmin</c> system column (no column of its own), which changes with
    /// every update of the row. <c>0</c> is "no row yet".
    /// </summary>
    public uint Version { get; set; }

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
