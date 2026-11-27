using System.Reflection;
using System.Runtime.CompilerServices;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

public class GuestPortalRegistryTests
{
    private static readonly Assembly[] Production =
    [
        typeof(PortalContext).Assembly,
        typeof(PortalContextFactory).Assembly,
        typeof(GuestPortalControllerBase).Assembly,
    ];

    [Fact]
    public async Task TestAssemblySection_AppearsWithoutCentralEdit()
    {
        var services = new ServiceCollection();
        services.AddGuestPortalRegistries([.. Production, typeof(GuestPortalRegistryTests).Assembly]);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sections = scope.ServiceProvider.GetServices<IGuestPortalSection>().ToList();

        Assert.Contains(sections, s => s is TestOnlySection);
        Assert.Contains(sections, s => s is StayPortalSection);

        using var world = new GuestPortalTestWorld();
        var overview = await world.OverviewService(sections: sections).GetAsync(await world.ContextAsync());

        var shown = Assert.Single(overview!.Sections, s => s.Key == TestOnlySection.TestKey);
        Assert.Equal((TestOnlySection.TestOrder, GuestPortalSectionStates.Attention, 2), (shown.Order, shown.State, shown.Badge));
        Assert.Equal(GuestPortalSectionKeys.Stay, overview.Sections[0].Key);
    }

    [Fact]
    public void Vocabulary_TheKeysOfTheSectionsAndTheWordsOfTheStates()
    {
        Assert.Equal(["stay", "checkin", "guide", "services", "chat", "documents", "review"], GuestPortalSectionKeys.All);
        Assert.Equal(
            ["available", "locked", "done", "attention"],
            Enum.GetValues<GuestPortalSectionState>().Select(GuestPortalSectionStates.WireName));
    }

    [Fact]
    public void ProductionRegistries_UniqueKeysOfTheVocabulary_AndTheDefaults()
    {
        var services = new ServiceCollection();
        services.AddGuestPortalRegistries(Production);
        var once = services.Count;
        services.AddGuestPortalRegistries(Production);
        Assert.Equal(once, services.Count);

        var sectionTypes = ImplementationsOf<IGuestPortalSection>(services);
        var keys = sectionTypes.Select(t => ((IGuestPortalSection)RuntimeHelpers.GetUninitializedObject(t)).Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.Contains(key, GuestPortalSectionKeys.All));
        Assert.Contains(typeof(StayPortalSection), sectionTypes);

        Assert.Contains(typeof(StayOverviewNextStep), ImplementationsOf<IGuestPortalNextStep>(services));
        Assert.Contains(typeof(DefaultGuestPortalPolicy), ImplementationsOf<IGuestPortalPolicy>(services));
        Assert.All(services, d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));
    }

    [Fact]
    public async Task StaySection_AndWelcomeStep_AreTheFirstSectionAndTheLastStep()
    {
        using var world = new GuestPortalTestWorld();
        var context = await world.ContextAsync();

        var stay = await new StayPortalSection().GetStateAsync(context);
        var welcome = await new StayOverviewNextStep().GetAsync(context);

        Assert.Equal((GuestPortalSectionKeys.Stay, 10, GuestPortalSectionState.Available), (new StayPortalSection().Key, new StayPortalSection().Order, stay!.State));
        Assert.Equal(1000, new StayOverviewNextStep().Priority);
        Assert.Equal(("stay.welcome", GuestPortalSectionKeys.Stay), (welcome!.Key, welcome.Section));
        Assert.True(await new DefaultGuestPortalPolicy().CanShowPaidServicesAsync(context));
    }

    private static List<Type> ImplementationsOf<TRegistry>(IServiceCollection services) =>
        services.Where(d => d.ServiceType == typeof(TRegistry)).Select(d => d.ImplementationType!).ToList();
}

public sealed class TestOnlySection : IGuestPortalSection
{
    public const string TestKey = "test-only";

    public const int TestOrder = 15;

    public string Key => TestKey;

    public int Order => TestOrder;

    public Task<GuestPortalSectionStatus?> GetStateAsync(PortalContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<GuestPortalSectionStatus?>(new GuestPortalSectionStatus(GuestPortalSectionState.Attention, 2));
}
