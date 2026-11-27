using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.GuestPortal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

public sealed class GuestPortalSetupTests
{
    [Fact]
    public void CompletionRequiresAllStepsAndSupportsExplicitOptOut()
    {
        var draft = GuestPortalSetup.Read(null);
        Assert.False(draft.HidePaidServicesOnAirbnb);
        Assert.False(draft.IsComplete(hasGuide: true));

        var configured = draft with
        {
            BrandConfirmed = true,
            GuideConfirmed = true,
            ExtrasConfirmed = true,
            RegionalSuppliers = false,
            ChatConfirmed = true,
            ReviewsEnabled = false,
            OtaRulesAcknowledged = true,
        };
        var saved = GuestPortalSetup.Read(configured.Write());

        Assert.False(saved.IsComplete(hasGuide: false));
        Assert.True(saved.IsComplete(hasGuide: true));
        Assert.False((saved with { RegionalSuppliers = null }).IsComplete(hasGuide: true));
        Assert.False((saved with { ReviewsEnabled = null }).IsComplete(hasGuide: true));
    }

    [Fact]
    public async Task AirbnbPolicyAppliesOnlyToTheConfiguredPropertyAndChannel()
    {
        using var world = new GuestPortalTestWorld();
        var context = await world.ContextAsync();
        var policy = new AirbnbGuestPortalPolicy(world.Db);

        Assert.True(await policy.CanShowPaidServicesAsync(context));

        var property = await world.Db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == context.PropertyId);
        property.GuestPortalSetupJson = new GuestPortalSetup { HidePaidServicesOnAirbnb = true }.Write();
        await world.Db.SaveChangesAsync();

        Assert.False(await policy.CanShowPaidServicesAsync(context));
        Assert.True(await policy.CanShowPaidServicesAsync(context with { Source = BookingSource.Direct }));
        Assert.True(await policy.CanShowPaidServicesAsync(context with { OrgId = Guid.NewGuid() }));
    }
}
