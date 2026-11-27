using Casazen.Core.GuestPortal;

namespace Casazen.Infrastructure.GuestPortal;

public sealed class StayPortalSection : IGuestPortalSection
{
    public string Key => GuestPortalSectionKeys.Stay;

    public int Order => 10;

    public Task<GuestPortalSectionStatus?> GetStateAsync(PortalContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<GuestPortalSectionStatus?>(new GuestPortalSectionStatus(GuestPortalSectionState.Available));
}

public sealed class StayOverviewNextStep : IGuestPortalNextStep
{
    public const string WelcomeKey = "stay.welcome";

    public int Priority => 1000;

    public Task<GuestPortalStep?> GetAsync(PortalContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<GuestPortalStep?>(new GuestPortalStep(WelcomeKey, GuestPortalSectionKeys.Stay));
}
