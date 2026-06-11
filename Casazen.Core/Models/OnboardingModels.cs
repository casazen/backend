namespace Casazen.Core.Models;

public record OnboardingConsentsInput(bool TosAccepted, string TosVersion, bool PrivacyAccepted, string PrivacyVersion, bool DpaAccepted, string DpaVersion, bool SubprocessorsAcknowledged, string SubprocessorsVersion, bool? MarketingOptIn = null);
public record OnboardingActivationStatus(bool RoleChosen, bool OrgProvisioned, bool ConsentsAccepted, bool PropertyCreated, bool SitePublished, bool FirstBookingTaken, bool Activated, string? PublicBookingUrl);

public enum ConsentValidationErrorType { Incomplete, StaleVersion }
public record ConsentValidationError(ConsentValidationErrorType Type, string Message, string[]? StaleDocuments = null);
