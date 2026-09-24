namespace Casazen.Core.Authorization;

/// <summary>
/// Host onboarding gate (PL-02, A1-05): the host contexts (<c>short-rent</c>, <c>long-rent</c>) and their permissions
/// are granted only after the onboarding is completed (<c>User.OnboardingCompletedAt</c>) and the Terms of Service,
/// Privacy notice and DPA of the <b>current</b> versions have been accepted for the user's org. Until then the API
/// answers 403 with <see cref="RequiredCode"/>, whatever the JWT roles or the DB role say. Admin and supplier contexts
/// are not host contexts and never wait for it.
/// </summary>
public static class HostOnboarding
{
    /// <summary>Stable <c>code</c> of the 403 ProblemDetails; the web app opens the onboarding, the mobile app its activation screen.</summary>
    public const string RequiredCode = "onboarding_required";

    /// <summary>Contexts withheld until the host onboarding is complete.</summary>
    public static readonly IReadOnlySet<string> HostContextKeys =
        new HashSet<string>(["short-rent", "long-rent"], StringComparer.OrdinalIgnoreCase);

    public static bool IsHostContext(string contextKey) => HostContextKeys.Contains(contextKey);
}

/// <summary>Where a user stands with the host onboarding gate (<see cref="HostOnboarding"/>).</summary>
/// <param name="OnboardingCompleted">The onboarding was completed at least once (<c>User.OnboardingCompletedAt</c>).</param>
/// <param name="ConsentsAccepted">
/// Terms of Service, Privacy notice and DPA of the current versions (<c>Legal:Documents:*:Version</c>) are recorded
/// for the user's current org.
/// </param>
public sealed record HostOnboardingStatus(bool OnboardingCompleted, bool ConsentsAccepted)
{
    /// <summary>No user row, or nothing done yet.</summary>
    public static HostOnboardingStatus NotStarted { get; } = new(false, false);

    /// <summary>True when the host contexts may be granted.</summary>
    public bool IsComplete => OnboardingCompleted && ConsentsAccepted;
}
