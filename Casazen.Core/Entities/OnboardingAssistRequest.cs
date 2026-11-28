using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A «Do it for me» request: the host asks CasaZen support to do a part, or a panel step, of the Octorate activation for it (NF-107, UX-15,
/// E10/F01/US01/T01). There is one open request (<see cref="OnboardingAssistStatus.Requested"/> or <see cref="OnboardingAssistStatus.InProgress"/>)
/// for each org, part, property and channel: the partial unique index <see cref="OpenRequestIndexName"/>, with null-equal semantics
/// (<c>NULLS NOT DISTINCT</c>), so a request about the whole org (no property) or of a context with no channel is held once too, while each
/// portal of a channel access (<see cref="ChannelCode"/>) has a request of its own.
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
    public const string OpenRequestIndexName = "UIX_OnboardingAssistRequests_OpenPerPartPropertyAndChannel";

    public const int ContextMaxLength = 30;

    public const int StaffNoteMaxLength = 500;

    public const int UserIdMaxLength = 255;

    public const int OutcomeMaxLength = 20;

    public const int HostNoteMaxLength = 500;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public OnboardingPartKey PartKey { get; set; }

    public Guid? PropertyId { get; set; }

    [Required]
    [MaxLength(ContextMaxLength)]
    public string Context { get; set; } = string.Empty;

    [MaxLength(OnboardingPanelStep.ChannelCodeMaxLength)]
    public string? ChannelCode { get; set; }

    public OnboardingAssistStatus Status { get; set; } = OnboardingAssistStatus.Requested;

    public WizardType? DraftWizardType { get; set; }

    public Guid? DraftEntityId { get; set; }

    [Required]
    [MaxLength(UserIdMaxLength)]
    public string RequestedByUserId { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UserIdMaxLength)]
    public string? AssignedToUserId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    [MaxLength(StaffNoteMaxLength)]
    public string? StaffNote { get; set; }

    public Guid? ActivityId { get; set; }

    /// <summary>
    /// What «Fatto» recorded (E20/F03/US01/T02): <see cref="OnboardingAssistOutcomes.Verified"/> when CasaZen read the result on the panel,
    /// <see cref="OnboardingAssistOutcomes.Declared"/> when the panel could not be read and the support person declared it, with its id in
    /// <see cref="AssignedToUserId"/>. Never a silent success: <c>null</c> while the request is open or after it was cancelled.
    /// </summary>
    [MaxLength(OutcomeMaxLength)]
    public string? Outcome { get; set; }

    [MaxLength(HostNoteMaxLength)]
    public string? HostNote { get; set; }

    public DateTime? ReopenedAt { get; set; }
}

public static class OnboardingAssistOutcomes
{
    public const string Verified = "verified";

    /// <summary>The panel could not be read (a structure created through the API exposes no police account, ISTAT is never readable): support declared it.</summary>
    public const string Declared = "declared";
}

public static class OnboardingAssistContexts
{
    public const string PanelPolice = "panelPolice";

    public const string PanelIstat = "panelIstat";

    public const string PanelChannelAccess = "panelChannelAccess";

    public const string Part = "part";
}
