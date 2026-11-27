using Casazen.Core.GuestPortal;

namespace Casazen.Infrastructure.GuestPortal;

/// <summary>
/// The section «stay» of the guest portal (E50/F01/US01/T02, NF-153): the summary of the stay, always offered and first. Its panels (payments, change,
/// cancellation, privacy) come with E53. Found by the registries like every other section: nothing central lists it.
/// </summary>
public sealed class StayPortalSection : IGuestPortalSection
{
    public string Key => GuestPortalSectionKeys.Stay;

    public int Order => 10;

    public Task<GuestPortalSectionStatus?> GetStateAsync(PortalContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<GuestPortalSectionStatus?>(new GuestPortalSectionStatus(GuestPortalSectionState.Available));
}

/// <summary>
/// The welcome of the stay (E50/F01/US01/T02): the next step when no other section proposes one, so the block in evidence is never empty. Priority 1000,
/// the last; the message key is <c>stay.welcome</c>, owned by the section «stay».
/// </summary>
public sealed class StayOverviewNextStep : IGuestPortalNextStep
{
    /// <summary>The message key of the welcome step.</summary>
    public const string WelcomeKey = "stay.welcome";

    public int Priority => 1000;

    public Task<GuestPortalStep?> GetAsync(PortalContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<GuestPortalStep?>(new GuestPortalStep(WelcomeKey, GuestPortalSectionKeys.Stay));
}
