using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A «Do it for me» request: the host asks CasaZen support to do a part, or a panel step, of the Octorate activation for it (NF-107, UX-15,
/// E10/F01/US01/T01). There is one open request (<see cref="OnboardingAssistStatus.Requested"/> or <see cref="OnboardingAssistStatus.InProgress"/>)
/// for each org, part and property: the partial unique index <see cref="OpenRequestIndexName"/>, with null-equal semantics
/// (<c>NULLS NOT DISTINCT</c>), so a request about the whole org (no property) is held once too.
/// </summary>
/// <remarks>
/// <para><b>A reference, never a copy.</b> The wizard draft the host was in is named by its type and entity (<see cref="DraftWizardType"/>,
/// <see cref="DraftEntityId"/>): a draft may hold personal data, so support reads it through the draft service, in the host's org, and
/// nothing of it is copied here. <see cref="StaffNote"/> is a short note of the support person and holds no personal data.</para>
/// <para>The users are opaque Auth0 subjects (<c>string</c>, not foreign keys). Rows follow their property (cascade, when there is one) and
/// carry the org (TN-2, restrict).</para>
/// </remarks>
[Table("OnboardingAssistRequests")]
public class OnboardingAssistRequest : ITenantOwned
{
    /// <summary>Name of the partial unique index on (<see cref="OrgId"/>, <see cref="PartKey"/>, <see cref="PropertyId"/>) of the open requests.</summary>
    public const string OpenRequestIndexName = "UIX_OnboardingAssistRequests_OpenPerPartAndProperty";

    /// <summary>Longest <see cref="Context"/>.</summary>
    public const int ContextMaxLength = 30;

    /// <summary>Longest <see cref="StaffNote"/>.</summary>
    public const int StaffNoteMaxLength = 500;

    /// <summary>Longest user id: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row (TN-2). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    public OnboardingPartKey PartKey { get; set; }

    /// <summary>The property the request is about (FK, CASCADE); <c>null</c> for a request about the whole org.</summary>
    public Guid? PropertyId { get; set; }

    /// <summary>What the host asked help with: one of <see cref="OnboardingAssistContexts"/>.</summary>
    [Required]
    [MaxLength(ContextMaxLength)]
    public string Context { get; set; } = string.Empty;

    public OnboardingAssistStatus Status { get; set; } = OnboardingAssistStatus.Requested;

    /// <summary>The wizard whose draft support may resume (with <see cref="DraftEntityId"/>); <c>null</c> when there is none.</summary>
    public WizardType? DraftWizardType { get; set; }

    /// <summary>What that wizard edits: a reference to the draft, never a copy of its content.</summary>
    public Guid? DraftEntityId { get; set; }

    /// <summary>Who asked: the Auth0 subject of the host's account.</summary>
    [Required]
    [MaxLength(UserIdMaxLength)]
    public string RequestedByUserId { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The support person who took the request (the Auth0 subject); <c>null</c> until somebody does.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? AssignedToUserId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the request was done or cancelled (UTC); <c>null</c> while it is open.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>A short note of the support person, with no personal data.</summary>
    [MaxLength(StaffNoteMaxLength)]
    public string? StaffNote { get; set; }

    /// <summary>The host's "To do" item (<c>InternalActivity</c>) that stands for the request; a reference, not a foreign key.</summary>
    public Guid? ActivityId { get; set; }
}

/// <summary>Stable values of <see cref="OnboardingAssistRequest.Context"/>: what the host asked help with. Never rename one.</summary>
public static class OnboardingAssistContexts
{
    /// <summary>The police (Alloggiati Web) connection in the Octorate panel.</summary>
    public const string PanelPolice = "panelPolice";

    /// <summary>The ISTAT connection in the Octorate panel.</summary>
    public const string PanelIstat = "panelIstat";

    /// <summary>The access to a channel given in the Octorate panel.</summary>
    public const string PanelChannelAccess = "panelChannelAccess";

    /// <summary>A whole part of the plan.</summary>
    public const string Part = "part";
}
