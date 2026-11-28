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
    public const string StepIndexName = "UIX_OnboardingPanelSteps_OrgId_PropertyId_Kind_ChannelCode";

    public const int ChannelCodeMaxLength = 40;

    public const int HandoffNonceHashLength = 64;

    public const int ResumeStepCodeMaxLength = 50;

    public const int OutcomeMaxLength = 20;

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

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

    public DateTime? HandoffExpiresAt { get; set; }

    public DateTime? CallbackConsumedAt { get; set; }

    public WizardType? ResumeWizardType { get; set; }

    public Guid? ResumeDraftEntityId { get; set; }

    [MaxLength(ResumeStepCodeMaxLength)]
    public string? ResumeStepCode { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? NextVerificationAt { get; set; }

    public int VerificationAttempts { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? VerifiedAt { get; set; }

    [MaxLength(OutcomeMaxLength)]
    public string? LastVerificationOutcome { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? CompletedByUserId { get; set; }

    public Guid? ActivityId { get; set; }
}

public static class PanelVerificationOutcomes
{
    public const string Verified = "verified";

    public const string NotValid = "notValid";

    public const string Unverifiable = "unverifiable";
}
