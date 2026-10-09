namespace Casazen.Core.Services;

/// <summary>Stable codes (HTTP 409) of the refused onboardings, see <see cref="IUserService.CompleteOnboardingAsync"/>.</summary>
public static class UserOnboardingErrors
{
    /// <summary>
    /// The caller is a member of an org (a DB membership of a host context with a role other than the owner's) and cannot
    /// complete the onboarding: it would overwrite that role and make the member the owner of the org it works for
    /// (AM-00, S2).
    /// </summary>
    public const string MemberCannotOnboard = "member_cannot_onboard";
}
