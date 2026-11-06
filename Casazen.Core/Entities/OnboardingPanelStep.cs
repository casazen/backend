using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A step Octorate lets the host do only from its own panel (police connection, ISTAT connection, channel access), as CasaZen guides it
/// (NF-107, ONB-04, E10/F01/US01/T01). The row exists from the moment the host is handed over to the panel, so CasaZen can resume the exact
/// step when the host comes back, tell who the callback belongs to, and rate-limit the background verification.
/// </summary>
/// <remarks>
/// <para><b>Unique key.</b> One row per org, property, kind and channel (unique index <see cref="StepIndexName"/>) with null-equal semantics
/// (<c>NULLS NOT DISTINCT</c>): two rows with no property, or with no channel, count as equal, so the police connection of an org that has no
/// property yet and the one of a property each have one row, and none of them can be started twice.</para>
/// <para><b>The nonce.</b> The hand-over carries a single-use nonce; only its SHA-256 is stored (<see cref="HandoffNonceHash"/>), never the
/// nonce, and the callback consumes it once (<see cref="CallbackConsumedAt"/>).</para>
/// <para><b>Resume.</b> <see cref="ResumeWizardType"/>, <see cref="ResumeDraftEntityId"/> and <see cref="ResumeStepCode"/> point at the draft
/// of the wizard and the step the host left: a reference, never a copy of what the host typed (a draft may hold personal data).</para>
/// <para>Rows follow their property (cascade, when there is one) and carry the org (TN-2, restrict). The users are opaque Auth0 subjects
/// (<c>string</c>, not foreign keys). No personal data, no credential.</para>
/// </remarks>
[Table("OnboardingPanelSteps")]
public class OnboardingPanelStep : ITenantOwned
{
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="PropertyId"/>, <see cref="Kind"/>, <see cref="ChannelCode"/>).</summary>
    public const string StepIndexName = "UIX_OnboardingPanelSteps_OrgId_PropertyId_Kind_ChannelCode";

    /// <summary>Longest <see cref="ChannelCode"/>.</summary>
    public const int ChannelCodeMaxLength = 40;

    /// <summary>Length of <see cref="HandoffNonceHash"/>: the hex of a SHA-256.</summary>
    public const int HandoffNonceHashLength = 64;

    /// <summary>Longest <see cref="ResumeStepCode"/>.</summary>
    public const int ResumeStepCodeMaxLength = 50;

    /// <summary>Longest <see cref="LastVerificationOutcome"/>.</summary>
    public const int OutcomeMaxLength = 20;

    /// <summary>Longest <see cref="CompletedByUserId"/>: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row (TN-2). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property the step is about (FK, CASCADE); <c>null</c> for a step of the whole org.</summary>
    public Guid? PropertyId { get; set; }

    public PanelStepKind Kind { get; set; }

    /// <summary>The code of the channel (<c>booking_xml</c>, <c>airbnb_xml</c>...) of a <see cref="PanelStepKind.ChannelAccess"/> step; <c>null</c> otherwise.</summary>
    [MaxLength(ChannelCodeMaxLength)]
    public string? ChannelCode { get; set; }

    public PanelStepStatus Status { get; set; } = PanelStepStatus.Started;

    public PanelHandoffMode HandoffMode { get; set; } = PanelHandoffMode.RedirectCallback;

    /// <summary>The SHA-256 of the single-use nonce of the hand-over, lowercase hex. The nonce itself is never stored.</summary>
    [MaxLength(HandoffNonceHashLength)]
    public string? HandoffNonceHash { get; set; }

    /// <summary>After this instant (UTC) the nonce is refused.</summary>
    public DateTime? HandoffExpiresAt { get; set; }

    /// <summary>When the callback consumed the nonce (UTC); <c>null</c> until it does.</summary>
    public DateTime? CallbackConsumedAt { get; set; }

    /// <summary>The wizard the host was in when it was handed over, to resume it.</summary>
    public WizardType? ResumeWizardType { get; set; }

    /// <summary>What that wizard edits (the id of its draft entity); a reference to the draft, never a copy of it.</summary>
    public Guid? ResumeDraftEntityId { get; set; }

    /// <summary>The step of the wizard to resume.</summary>
    [MaxLength(ResumeStepCodeMaxLength)]
    public string? ResumeStepCode { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the background verification may run next (UTC); <c>null</c> = no verification is scheduled.</summary>
    public DateTime? NextVerificationAt { get; set; }

    /// <summary>How many times the verification ran for this hand-over: the count that rate-limits it.</summary>
    public int VerificationAttempts { get; set; }

    /// <summary>When the step was completed, by the verification or by the host's confirmation (UTC).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>When CasaZen itself verified the step (UTC); <c>null</c> when it was not or could not be verified.</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>What the last verification found: one of <see cref="PanelVerificationOutcomes"/>; <c>null</c> = never verified.</summary>
    [MaxLength(OutcomeMaxLength)]
    public string? LastVerificationOutcome { get; set; }

    /// <summary>Who said the step is done (the Auth0 subject); <c>null</c> when nobody did.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? CompletedByUserId { get; set; }

    /// <summary>The "To do" item (<c>InternalActivity</c>) of a step left half-way; a reference, not a foreign key.</summary>
    public Guid? ActivityId { get; set; }
}

/// <summary>Stable values of <see cref="OnboardingPanelStep.LastVerificationOutcome"/>: never rename one.</summary>
public static class PanelVerificationOutcomes
{
    /// <summary>The verification found the step done.</summary>
    public const string Verified = "verified";

    /// <summary>The verification found the step not done or not valid.</summary>
    public const string NotValid = "notValid";

    /// <summary>The verification could not tell (the panel offers no way to check it, or the check failed): the host confirms.</summary>
    public const string Unverifiable = "unverifiable";
}
