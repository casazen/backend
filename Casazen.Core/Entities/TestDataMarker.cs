using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Points at one row written by the demo seed. A reset deletes only rows that have a marker of origin
/// <see cref="TestDataOrigins.Showcase"/>. Compliance fixtures on a host's own property are marked too, and are left
/// in place: the CIN and the files are invented, the property is not.
/// </summary>
[Table("TestDataMarkers")]
public class TestDataMarker : ITenantOwned
{
    public const int EntityTypeMaxLength = 64;
    public const int OriginMaxLength = 32;
    public const int TemplateKeyMaxLength = 80;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required, MaxLength(EntityTypeMaxLength)]
    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    /// <summary><see cref="TestDataOrigins.Compliance"/> or <see cref="TestDataOrigins.Showcase"/>.</summary>
    [Required, MaxLength(OriginMaxLength)]
    public string Origin { get; set; } = string.Empty;

    /// <summary>Stable key inside <c>DemoShowcaseCatalog</c> when <see cref="Origin"/> is the showcase.</summary>
    [MaxLength(TemplateKeyMaxLength)]
    public string? TemplateKey { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Why a row was marked as test data.</summary>
public static class TestDataOrigins
{
    /// <summary>Invented CIN, document or checklist written onto a property that already existed.</summary>
    public const string Compliance = "compliance";

    /// <summary>Row created by the showcase template. The reset deletes these and recreates them.</summary>
    public const string Showcase = "showcase";
}

/// <summary>Values of <see cref="TestDataMarker.EntityType"/>. Append a new one when the seed writes a new table.</summary>
public static class TestDataEntities
{
    public const string Property = "Property";
    public const string PropertyDocument = "PropertyDocument";
    public const string SafetyChecklist = "PropertySafetyChecklist";
    public const string Guest = "Guest";
    public const string Booking = "Booking";
    public const string HostInvite = "HostInviteRecord";
}
