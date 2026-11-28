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
    public const string OrgPropertyIndexName = "UIX_PropertyAssistantSettings_OrgId_PropertyId";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public bool? Enabled { get; set; }

    public AssistantTone? Tone { get; set; }

    [MaxLength(OrgAssistantRules.SignatureMaxLength)]
    public string? Signature { get; set; }

    public TimeOnly? HoursFrom { get; set; }

    public TimeOnly? HoursTo { get; set; }

    [Precision(4, 3)]
    public decimal? ConfidenceThreshold { get; set; }

    public List<string>? ForbiddenTopics { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(OrgAssistantRules.UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
