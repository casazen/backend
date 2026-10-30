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
    /// <summary>The supplier org (<see cref="SupplierProfile.OrgId"/>): the key, and the foreign key.</summary>
    [Key]
    public Guid OrgId { get; set; }

    /// <summary>Format of the three documents (<see cref="ShowcaseContentLimits.SchemaVersion"/>); a new format gets a new number.</summary>
    public int SchemaVersion { get; set; } = ShowcaseContentLimits.SchemaVersion;

    /// <summary>The draft the supplier is writing (the editor autosaves into it); never shown to customers.</summary>
    [Column(TypeName = "jsonb")]
    public string DraftJson { get; set; } = "{}";

    /// <summary>The version the customers read; <c>null</c> until the first publication.</summary>
    [Column(TypeName = "jsonb")]
    public string? PublishedJson { get; set; }

    /// <summary>
    /// The version published before the last publication, kept for "Annulla" while <see cref="UndoUntil"/> is in the future;
    /// <c>null</c> when the last publication was the first (undoing it unpublishes) or when no undo is possible.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? PreviousPublishedJson { get; set; }

    /// <summary>When <see cref="PreviousPublishedJson"/> was published (restored with it).</summary>
    public DateTime? PreviousPublishedAt { get; set; }

    /// <summary>
    /// Until when the last publication can be undone; <c>null</c> when it cannot. Set by a publication, cleared by any other change
    /// (a save, a discard, a pause) and once the time has passed.
    /// </summary>
    public DateTime? UndoUntil { get; set; }

    /// <summary>The last save of the draft (UTC).</summary>
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The last publication (UTC); <c>null</c> while nothing is published.</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>When the supplier paused the showcase (UTC); <c>null</c> while it is not paused.</summary>
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
