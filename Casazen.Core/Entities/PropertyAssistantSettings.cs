using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// The exceptions of one property to the rules of its organization (E05/F05/US01/T01, NF-66): one row per property (unique index) holding only what the
/// property does differently. <b>A <c>null</c> field means «as the organization»</b>; the effective value of a property is its own value, else the
/// organization's (<c>IAssistantRulesService.ResolveEffectiveAsync</c>). A row that was never written means no exception at all.
/// </summary>
/// <remarks>
/// <see cref="Enabled"/> can only switch the assistant off for the property: the effective switch is on only when the organization's is on and the property does
/// not turn it off. The row goes with its property (cascade).
/// </remarks>
[Table("PropertyAssistantSettings")]
public class PropertyAssistantSettings : ITenantOwned
{
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="PropertyId"/>).</summary>
    public const string OrgPropertyIndexName = "UIX_PropertyAssistantSettings_OrgId_PropertyId";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, the one of its property.</summary>
    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    /// <summary><c>false</c> = the assistant is off for this property whatever the organization says; <c>null</c> = as the organization.</summary>
    public bool? Enabled { get; set; }

    public AssistantTone? Tone { get; set; }

    [MaxLength(OrgAssistantRules.SignatureMaxLength)]
    public string? Signature { get; set; }

    /// <summary>With <see cref="HoursTo"/>: the hours of this property in which the assistant may send by itself; both <c>null</c> = as the organization.</summary>
    public TimeOnly? HoursFrom { get; set; }

    public TimeOnly? HoursTo { get; set; }

    [Precision(4, 3)]
    public decimal? ConfidenceThreshold { get; set; }

    /// <summary>
    /// The topics and situations never answered for this property; <c>null</c> = as the organization. When it is set it <b>replaces</b> the organization's
    /// list: an empty list means «nothing forbidden for this property». Stored as <c>text[]</c>.
    /// </summary>
    public List<string>? ForbiddenTopics { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The user who last saved the exceptions (<c>User.Id</c>), an opaque id with no foreign key.</summary>
    [MaxLength(OrgAssistantRules.UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
