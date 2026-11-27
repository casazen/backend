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
    /// <summary>Name of the unique index on <see cref="OrgId"/>.</summary>
    public const string OrgIndexName = "UIX_OrgAssistantRules_OrgId";

    public const int SignatureMaxLength = 300;

    public const int UpdatedByUserIdMaxLength = 255;

    /// <summary>The threshold of a new organization (PO-08): 0.85.</summary>
    public const decimal DefaultConfidenceThreshold = 0.85m;

    /// <summary>The lowest threshold an organization may set.</summary>
    public const decimal MinConfidenceThreshold = 0.50m;

    /// <summary>The highest threshold an organization may set.</summary>
    public const decimal MaxConfidenceThreshold = 0.99m;

    /// <summary>The languages of a new organization: Italian and English.</summary>
    public static IReadOnlyList<string> DefaultLanguages { get; } = ["it", "en"];

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, and its key: one row per organization.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The organization switch: <c>false</c> (the default) = the assistant does nothing for any property of the organization.</summary>
    public bool Enabled { get; set; }

    public AssistantTone Tone { get; set; } = AssistantTone.Neutral;

    /// <summary>The languages the assistant may write in: two-letter lower-case codes, at least one. Stored as <c>text[]</c>.</summary>
    public List<string> Languages { get; set; } = [.. DefaultLanguages];

    /// <summary>The closing of the drafts, at most <see cref="SignatureMaxLength"/> characters; <c>null</c> = none.</summary>
    [MaxLength(SignatureMaxLength)]
    public string? Signature { get; set; }

    /// <summary>
    /// With <see cref="HoursTo"/>: the hours in which the assistant may send by itself, on the clock of the property (a window that crosses midnight is
    /// allowed); both <c>null</c> = at any time.
    /// </summary>
    public TimeOnly? HoursFrom { get; set; }

    public TimeOnly? HoursTo { get; set; }

    /// <summary>
    /// The topics (<see cref="KnowledgeTopic"/> names) and situations (<see cref="AssistantSituation"/> names) the assistant may answer; <b>empty = no
    /// restriction by this list</b> (the default): the forbidden ones are still left out. Stored as <c>text[]</c>.
    /// </summary>
    public List<string> AllowedTopics { get; set; } = [];

    /// <summary>The topics and situations the assistant never answers: a message about one of them goes to a person. Stored as <c>text[]</c>.</summary>
    public List<string> ForbiddenTopics { get; set; } = [];

    /// <summary>The score above which a draft may be sent by itself, 0.50 to 0.99 (PO-08: 0.85 to start).</summary>
    [Precision(4, 3)]
    public decimal ConfidenceThreshold { get; set; } = DefaultConfidenceThreshold;

    /// <summary>Starts at 1 and grows by one with every change that affects drafting. The concurrency token of the row.</summary>
    public int InstructionVersion { get; set; } = 1;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The user who last saved the rules (<c>User.Id</c>), an opaque id with no foreign key.</summary>
    [MaxLength(UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
