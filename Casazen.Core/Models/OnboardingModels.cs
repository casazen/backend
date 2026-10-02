namespace Casazen.Core.Models;

public record OnboardingConsentsInput(bool TosAccepted, string TosVersion, bool PrivacyAccepted, string PrivacyVersion, bool DpaAccepted, string DpaVersion, bool SubprocessorsAcknowledged, string SubprocessorsVersion, bool? MarketingOptIn = null);
/// <summary>
/// Activation of the caller's org (<c>GET /api/onboarding/status</c>). The flags and <see cref="Steps"/> are derived from
/// the stored state, never from a manual flag (PLG-AC6): <see cref="SitePublished"/> is true only when a guest can
/// really open a published property of the org and pay for it (PL-15, A1-37, A3-26).
/// </summary>
public record OnboardingActivationStatus(bool RoleChosen, bool OrgProvisioned, bool ConsentsAccepted, bool PropertyCreated, bool SitePublished, bool FirstBookingTaken, bool Activated, string? PublicBookingUrl)
{
    /// <summary>The checklist the host sees, in the order to do it (<see cref="ActivationChecklist"/>).</summary>
    public IReadOnlyList<ActivationChecklistStep> Steps { get; init; } = [];
}

/// <summary>
/// One step of the activation checklist. <see cref="State"/> is an <see cref="ActivationStepStates"/> value;
/// <see cref="Reason"/> (an <see cref="ActivationStepReasons"/> value) says why a step is not done; <see cref="Done"/> /
/// <see cref="Total"/> count the properties concerned for the steps that count them. All stable codes: the client
/// translates them.
/// </summary>
public record ActivationChecklistStep(string Key, string State, string? Reason = null, int? Done = null, int? Total = null);

public enum ConsentValidationErrorType { Incomplete, StaleVersion }
public record ConsentValidationError(ConsentValidationErrorType Type, string MessageKey, string[]? StaleDocuments = null);
