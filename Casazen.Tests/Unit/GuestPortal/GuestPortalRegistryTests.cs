using System.Reflection;
using System.Runtime.CompilerServices;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

/// <summary>
/// E50/F01/US01/T02 (NF-153, AC-NF153-2, decision A4): the registries of the guest portal. A class that implements a registry interface takes effect with
/// no edit to an existing file; the words of the sections and of their states are the fixed vocabulary; the keys of the sections are unique. No exact
/// list of the sections is photographed here: the later epics add theirs.
/// </summary>
public class GuestPortalRegistryTests
{
    /// <summary>The assemblies Program.cs scans.</summary>
    private static readonly Assembly[] Production =
    [
        typeof(PortalContext).Assembly,
        typeof(PortalContextFactory).Assembly,
        typeof(GuestPortalControllerBase).Assembly,
    ];

    /// <summary>A section only this assembly knows, with a key of its own: nothing central names it.</summary>
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

        // And the overview shows it, in its order, with its state and badge.
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

    /// <summary>
    /// The sections, steps and policies of the application: every section has a key of the vocabulary, no key twice; the stay section, the welcome step
    /// and the default policy are there; scanning twice registers nothing twice. The keys are read without building the sections (their key is a
    /// constant), so a section with dependencies of its own needs no change here.
    /// </summary>
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

/// <summary>A section of the test assembly: found by the scan of the registries like the ones of the epics.</summary>
public sealed class TestOnlySection : IGuestPortalSection
{
    public const string TestKey = "test-only";

    public const int TestOrder = 15;

    public string Key => TestKey;

    public int Order => TestOrder;

    public Task<GuestPortalSectionStatus?> GetStateAsync(PortalContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<GuestPortalSectionStatus?>(new GuestPortalSectionStatus(GuestPortalSectionState.Attention, 2));
}
