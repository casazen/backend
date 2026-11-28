namespace Casazen.Core.Services;

/// <summary>
/// Attribution of a signup as sent by the web app (already checked against
/// <see cref="Casazen.Core.Validation.SignupAttributionRules"/>). <see cref="Comune"/> is a slug or an ISTAT code.
/// </summary>
public sealed record SignupAttributionInput(
    string? UtmSource,
    string? UtmMedium,
    string? UtmCampaign,
    string? UtmTerm,
    string? UtmContent,
    string? Comune,
    string? LandingPath,
    string? ReferrerHost);

public sealed record SignupAttributionRecord(
    Guid OrgId,
    DateTime RecordedAt,
    string? UtmSource,
    string? UtmMedium,
    string? UtmCampaign,
    string? UtmTerm,
    string? UtmContent,
    string? ComuneCode,
    string? ComuneName,
    string? LandingPath,
    string? ReferrerHost);

public interface ISignupAttributionService
{
    public const string OnboardingRequiredCode = "signup_attribution_onboarding_required";

    public const string UnknownComuneCode = "signup_attribution_unknown_comune";

    /// <summary>
    /// Records the attribution of the caller's org, created by the first onboarding. Idempotent: <c>true</c> when this
    /// call stored it, <c>false</c> when the org already has one (the first attribution is never overwritten).
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// The caller has no org or has not completed the onboarding (<see cref="OnboardingRequiredCode"/>), or the comune
    /// is unknown (<see cref="UnknownComuneCode"/>).
    /// </exception>
    Task<bool> RecordAsync(string userId, SignupAttributionInput input, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<SignupAttributionRecord> Items, int TotalCount)> ListAsync(
        DateTime? recordedFromUtc,
        string? comuneCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
