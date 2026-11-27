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
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="Situation"/>) of the rows of the organization (no property).</summary>
    public const string OrgSituationIndexName = "UIX_AssistantSituationSettings_OrgId_Situation";

    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="PropertyId"/>, <see cref="Situation"/>) of the exceptions of the properties.</summary>
    public const string PropertySituationIndexName = "UIX_AssistantSituationSettings_OrgId_PropertyId_Situation";

    /// <summary>Longest <see cref="ResponseTemplate"/>, in characters of the clear text.</summary>
    public const int TemplateMaxLength = 1000;

    public const int UpdatedByUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row. Never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property of an exception; <c>null</c> = the organization's default for the situation.</summary>
    public Guid? PropertyId { get; set; }

    public AssistantSituation Situation { get; set; }

    public AssistantMode Mode { get; set; } = AssistantMode.Draft;

    /// <summary>The text of the answer, with <c>{{variables}}</c>; <c>null</c> = none (the answer is written from the knowledge base). <b>Encrypted at rest.</b></summary>
    public string? ResponseTemplate { get; set; }

    /// <summary>The topic of the knowledge base the answer comes from when there is no template; <c>null</c> = the one the catalog gives the situation.</summary>
    public KnowledgeTopic? ResponseSource { get; set; }

    /// <summary>
    /// The start of the current trust period (UTC): the creation of the row or the last change of the mode (PO-08: two weeks of draft only, measured from
    /// here). A change of the template or of the source does not move it.
    /// </summary>
    public DateTime ModeSince { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The user who last saved the row (<c>User.Id</c>), an opaque id with no foreign key.</summary>
    [MaxLength(UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
