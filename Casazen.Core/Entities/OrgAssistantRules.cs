using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// How an organization wants its assistant to write to guests (E05/F05/US01/T01, NF-66): the organization switch, the tone, the languages, the signature, the
/// hours in which it may send by itself, the topics it may and may not answer and the confidence threshold (PO-08). One row per organization (unique index); an
/// organization with no row has the defaults, which <c>IAssistantRulesService.GetAsync</c> returns without writing. A property may differ from these values
/// (<see cref="PropertyAssistantSettings"/>).
/// </summary>
/// <remarks>
/// <para><see cref="InstructionVersion"/> grows by one with every change that affects what a draft says (the rules, the knowledge entries, the situations, the
/// lessons), in the same unit of work as the change: the decision log keeps the version a draft was written with (AC-NF67-1). It is the concurrency token of the
/// row, so two changes at the same moment cannot both claim the same version.</para>
/// <para>No personal data: the signature is the host's own, public one.</para>
/// </remarks>
[Table("OrgAssistantRules")]
public class OrgAssistantRules : ITenantOwned
{
    public const string OrgIndexName = "UIX_OrgAssistantRules_OrgId";

    public const int SignatureMaxLength = 300;

    public const int UpdatedByUserIdMaxLength = 255;

    public const decimal DefaultConfidenceThreshold = 0.85m;

    public const decimal MinConfidenceThreshold = 0.50m;

    public const decimal MaxConfidenceThreshold = 0.99m;

    public static IReadOnlyList<string> DefaultLanguages { get; } = ["it", "en"];

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public bool Enabled { get; set; }

    public AssistantTone Tone { get; set; } = AssistantTone.Neutral;

    public List<string> Languages { get; set; } = [.. DefaultLanguages];

    [MaxLength(SignatureMaxLength)]
    public string? Signature { get; set; }

    public TimeOnly? HoursFrom { get; set; }

    public TimeOnly? HoursTo { get; set; }

    public List<string> AllowedTopics { get; set; } = [];

    public List<string> ForbiddenTopics { get; set; } = [];

    [Precision(4, 3)]
    public decimal ConfidenceThreshold { get; set; } = DefaultConfidenceThreshold;

    /// <summary>Starts at 1 and grows by one with every change that affects drafting. The concurrency token of the row.</summary>
    public int InstructionVersion { get; set; } = 1;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
