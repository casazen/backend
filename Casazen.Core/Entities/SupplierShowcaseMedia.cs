using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Entities;

/// <summary>
/// One image a supplier uploaded for its public showcase (SP-13b, <c>api/supplier/showcase/media</c>): the logo, the cover or a photo of a
/// job (or its "before" photo). The row is the <b>inventory</b> of the files the supplier has in its showcase folder of the public bucket
/// (<c>suppliers/{orgId}/showcase/</c>): it counts them against the limits, says which ones the supplier may delete and which ones the daily
/// cleanup may remove. The content of the showcase (<see cref="SupplierShowcaseContent"/>) points to a file by its absolute public URL; this
/// row only says that the file exists and is the supplier's.
/// </summary>
/// <remarks>
/// <para><b>Tenancy.</b> Keyed by the supplier org (<see cref="OrgId"/> = <see cref="SupplierProfile.OrgId"/>), like
/// <see cref="SupplierShowcaseContent"/>: it is <b>not</b> <c>ITenantOwned</c> (a supplier acts as <c>User.SupplierOrgId</c> and a
/// supplier-only account has no <c>User.OrgId</c>). It is in the allow-list of <c>TenantQueryFilterArchitectureTests</c> and every read and
/// write filters by <see cref="OrgId"/> explicitly (<c>SupplierShowcaseMediaService</c> and the content service, which only reads the keys of
/// the supplier's own rows).</para>
/// <para><b>What it never holds.</b> No file name of the upload and no personal data: the object has a random name, and the image was
/// stripped of its metadata (EXIF, GPS, XMP, comments) before it was stored.</para>
/// </remarks>
[Table("SupplierShowcaseMedia")]
public class SupplierShowcaseMedia
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The supplier org the file belongs to (<see cref="SupplierProfile.OrgId"/>).</summary>
    [Required]
    public Guid OrgId { get; set; }

    /// <summary>What the file is for: one of <see cref="ShowcaseMediaKinds"/>.</summary>
    [Required, MaxLength(ShowcaseMediaLimits.KindMaxLength)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// The key of the object in the public bucket (<c>suppliers/{orgId}/showcase/{kind}-{random}.{ext}</c>). The absolute address is
    /// always derived from it, so a change of the public base URL never leaves a stale copy here.
    /// </summary>
    [Required, MaxLength(ShowcaseMediaLimits.StorageKeyMaxLength)]
    public string StorageKey { get; set; } = string.Empty;

    /// <summary><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>: what the file is, read from its content.</summary>
    [Required, MaxLength(ShowcaseMediaLimits.ContentTypeMaxLength)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>The size of the stored file, after its metadata was removed.</summary>
    public int Bytes { get; set; }

    /// <summary>The size of the picture as a browser draws it (a photo taken on its side has them swapped); <c>null</c> when it could not be told.</summary>
    public int? Width { get; set; }

    /// <inheritdoc cref="Width"/>
    public int? Height { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
