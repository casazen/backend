using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One level of the floor plan of a short-stay property (DB-11a): the picture the host uploaded (PNG or JPEG) with a name the guest
/// reads in Italian and, if the host wrote it, in English. The property page of the booking site shows the levels lowest
/// <see cref="Level"/> first. A property with no row has no floor plan, which is every property that existed before the table;
/// nothing here reads into a booking, a price or a refund.
/// </summary>
/// <remarks>
/// The image is an object of the public bucket in the property's own folder (<c>StorageKeys.PropertyFloorPlan</c>) and
/// <see cref="ImageUrl"/> is its absolute URL, like the photos of the gallery. Rows follow their property (cascade) and carry
/// its <see cref="OrgId"/> (TN-2), so the tenant filter and the checks of the host forms apply as to every child row.
/// </remarks>
[Table("PropertyFloorPlans")]
public class PropertyFloorPlan : ITenantOwned
{
    /// <summary>At most this many levels a property can have: a house over six floors is not drawn level by level.</summary>
    public const int MaxPerProperty = 6;

    public const int NameMaxLength = 60;

    public const int ImageUrlMaxLength = 2000;

    /// <summary>The living area of one level, in square metres, <see cref="MinAreaSqm"/> to <see cref="MaxAreaSqm"/>.</summary>
    public const int MinAreaSqm = 1;

    public const int MaxAreaSqm = 9999;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }

    public virtual Property Property { get; set; } = null!;

    /// <summary>Tenant of the row, copied from <see cref="Property"/> when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>The floor this plan draws, <c>SitePropertyProfileLimits.MinFloorLevel</c> to <c>MaxFloorLevel</c>, 0 = ground floor. The page lists the levels lowest floor first.</summary>
    public int Level { get; set; }

    /// <summary>Order of the rows of one floor (the order they were uploaded in), 0 first.</summary>
    public int Position { get; set; }

    /// <summary>What the guest reads: «Piano terra», «Primo piano», «Mansarda».</summary>
    [Required, MaxLength(NameMaxLength)]
    public string NameIt { get; set; } = string.Empty;

    /// <summary>The same in English; null = the host wrote none and the page shows <see cref="NameIt"/>.</summary>
    [MaxLength(NameMaxLength)]
    public string? NameEn { get; set; }

    /// <summary>Absolute public URL of the image in the property's own folder of the public bucket.</summary>
    [Required, MaxLength(ImageUrlMaxLength)]
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>Width of the image in pixels, read from the file when it was uploaded; null = not known.</summary>
    public int? ImageWidth { get; set; }

    /// <summary>Height of the image in pixels, read from the file when it was uploaded; null = not known.</summary>
    public int? ImageHeight { get; set; }

    /// <summary>The living area of this level in square metres; null = the host did not say.</summary>
    public int? AreaSqm { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
