using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// What the assistant does when it recognizes a situation, for the organization or, with <see cref="PropertyId"/> set, as an exception of one property
/// (E05/F05/US01/T01, NF-66, NF-67): the mode (escalate, draft, automatic, automatic with exceptions), the text of the answer when there is a template, and the
/// topic of the knowledge base it comes from when there is none. At most one row for the organization and situation, and one for each property and situation
/// (two partial unique indexes). A situation with no row has the default of <c>AssistantSituationCatalog</c> (T03): <see cref="AssistantMode.Draft"/>, and
/// <see cref="AssistantMode.Escalate"/> for an emergency. A property row is a whole row: it replaces the organization's, it does not merge with it.
/// </summary>
/// <remarks>
/// <see cref="ResponseTemplate"/> is the host's own text, with the <c>{{variables}}</c> of <c>MessageTemplateRenderer</c>: it can hold a note or a code, so it is
/// encrypted at rest with the purpose <c>Casazen.Assistant.Content</c> (<c>EncryptedColumns</c>) and is never logged. Its length (<see cref="TemplateMaxLength"/>)
/// is the service's rule, not a column length.
/// </remarks>
[Table("AssistantSituationSettings")]
public class AssistantSituationSetting : ITenantOwned
{
    public const string OrgSituationIndexName = "UIX_AssistantSituationSettings_OrgId_Situation";

    public const string PropertySituationIndexName = "UIX_AssistantSituationSettings_OrgId_PropertyId_Situation";

    public const int TemplateMaxLength = 1000;

    public const int UpdatedByUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid? PropertyId { get; set; }

    public AssistantSituation Situation { get; set; }

    public AssistantMode Mode { get; set; } = AssistantMode.Draft;

    public string? ResponseTemplate { get; set; }

    public KnowledgeTopic? ResponseSource { get; set; }

    public DateTime ModeSince { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
