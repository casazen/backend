using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Services;
using Casazen.Web.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E29/F02/US01/T02 (NF-129): the onboarding part «Guest registration». It is done only when every active Swiss property the caller reaches has its municipality
/// and, in Ticino, the structure number; each property that lacks something is named with its reason and the step of the wizard to reopen; an organization with
/// no Swiss property has nothing to do here; only the properties the caller reaches (and of its org) are counted, and an empty reach is an empty answer.
/// </summary>
public class GuestRegistrationPartEvaluatorTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync()
    {
        _world = await HostScopeScenario.SeedAsync(_db);
        await SwissMunicipalitySeed.ApplyAsync(_db, SwissMunicipalitySeed.ReadEmbedded()!, TimeProvider.System, Ct);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Evaluate_AnOrganizationWithNoSwissProperty_IsNotAvailable_AndNoItalianPropertyIsCounted()
    {
        var facts = await Evaluator().EvaluateAsync(Context(_world.OrgWide, _world.Granted.Id, _world.Hidden.Id), Ct);

        Assert.False(facts.Provided);
        Assert.Equal(OnboardingPartReasons.NoSwissProperty, facts.Reason);
        Assert.Equal("no_swiss_property", OnboardingPartReasons.NoSwissProperty);
        Assert.Empty(facts.Missing);
        Assert.False(facts.IsComplete);
    }

    /// <summary>An empty reach is an empty answer, never «every property of the org»: the Swiss property that lacks its number is not read for a caller that reaches none.</summary>
    [Fact]
    public async Task Evaluate_AnEmptyReach_IsNotAvailable_NeverEveryPropertyOfTheOrg()
    {
        await SwissAsync(_world.Granted, "6900", "Lugano");

        var facts = await Evaluator().EvaluateAsync(Context(_world.Restricted), Ct);

        Assert.False(facts.Provided);
        Assert.Equal(OnboardingPartReasons.NoSwissProperty, facts.Reason);
        Assert.Empty(facts.Missing);
    }

    /// <summary>Proof (data, acceptance criterion 3): a Ticino property without a structure number keeps the part open and is named; with the number the part is done.</summary>
    [Fact]
    public async Task Evaluate_ATicinoPropertyWithoutAStructureNumber_IsOpen_AndNamesTheProperty_Test()
    {
        await SwissAsync(_world.Granted, "6900", "Lugano");

        var open = await Evaluator().EvaluateAsync(Context(_world.OrgWide, _world.Granted.Id), Ct);

        Assert.True(open.Provided);
        Assert.False(open.IsComplete);
        Assert.Equal((0, 1), (open.Done, open.Total));
        Assert.Equal(OnboardingPartReasons.TicinoStructureIdMissing, open.Reason);
        var item = Assert.Single(open.Missing);
        Assert.Equal((_world.Granted.Id, "ticino_structure_id_missing", "guest-registration"), (item.PropertyId, item.Reason, item.Code));

        await SetAsync(_world.Granted, p => p.TicinoStructureId = "NL-12345678");

        var done = await Evaluator().EvaluateAsync(Context(_world.OrgWide, _world.Granted.Id), Ct);
        Assert.True(done.IsComplete);
        Assert.Equal((1, 1, (string?)null), (done.Done, done.Total, done.Reason));
        Assert.Empty(done.Missing);
    }

    /// <summary>A municipality comes before a structure number (nobody knows a property is in Ticino without it), and the items follow the order of the plan.</summary>
    [Fact]
    public async Task Evaluate_AMunicipalityBeforeAStructureNumber_AndTheItemsInTheOrderOfThePlan()
    {
        await SwissAsync(_world.Granted, "6900", "Lugano");
        await SwissAsync(_world.Hidden, "6900", "Ticino");

        var facts = await Evaluator().EvaluateAsync(Context(_world.OrgWide, _world.Granted.Id, _world.Hidden.Id), Ct);

        Assert.Equal(OnboardingPartReasons.SwissMunicipalityMissing, facts.Reason);
        Assert.Equal(
            [(_world.Granted.Id, "ticino_structure_id_missing"), (_world.Hidden.Id, "swiss_municipality_missing")],
            facts.Missing.Select(m => (m.PropertyId, m.Reason)));
        Assert.All(facts.Missing, m => Assert.Equal(GuestRegistrationPartEvaluator.StepCode, m.Code));
    }

    [Fact]
    public async Task Evaluate_OnlyTheSwissProperties_AreCounted()
    {
        await SwissAsync(_world.Granted, "8001", "Zürich");

        var facts = await Evaluator().EvaluateAsync(Context(_world.OrgWide, _world.Granted.Id, _world.Hidden.Id), Ct);

        Assert.True(facts.IsComplete);
        Assert.Equal((1, 1), (facts.Done, facts.Total));
    }

    /// <summary>Proof (permission, AM-03): a collaborator «Solo alcuni» sees only the properties it was given: the other Swiss property of the org is not counted nor named.</summary>
    [Fact]
    public async Task Evaluate_CountsOnlyThePropertiesTheCallerReaches_Test()
    {
        await SwissAsync(_world.Granted, "8001", "Zürich");
        await SwissAsync(_world.Hidden, "6900", "Lugano");

        var restricted = await Evaluator().EvaluateAsync(Context(_world.Restricted, _world.Granted.Id), Ct);
        var orgWide = await Evaluator().EvaluateAsync(Context(_world.OrgWide, _world.Granted.Id, _world.Hidden.Id), Ct);

        Assert.True(restricted.IsComplete);
        Assert.Equal((1, 1), (restricted.Done, restricted.Total));
        Assert.Empty(restricted.Missing);
        Assert.False(orgWide.IsComplete);
        Assert.Equal((1, 2), (orgWide.Done, orgWide.Total));
        Assert.Equal(_world.Hidden.Id, Assert.Single(orgWide.Missing).PropertyId);
    }

    /// <summary>Another organization's property is never counted, even when its id is among the ones given.</summary>
    [Fact]
    public async Task Evaluate_NeverCountsAPropertyOfAnotherOrganization_Test()
    {
        await SwissAsync(_world.OtherOrgProperty, "6900", "Lugano");

        var facts = await Evaluator().EvaluateAsync(Context(_world.OrgWide, _world.OtherOrgProperty.Id), Ct);

        Assert.False(facts.Provided);
        Assert.Empty(facts.Missing);
    }

    /// <summary>The plan is read often: a portfolio of hundreds of houses is not listed whole (the counts say how many lack something in all).</summary>
    [Fact]
    public async Task Evaluate_ListsAtMostFiftyProperties_AndCountsThemAll()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 60; i++)
        {
            var property = HostScopeScenario.NewProperty(_world.OrgId, _world.OwnerUserId, $"Chalet {i:D2}");
            property.Country = "CH";
            property.PostalCode = "6900";
            property.City = "Lugano";
            _db.Properties.Add(property);
            ids.Add(property.Id);
        }

        await _db.SaveChangesAsync(Ct);

        var facts = await Evaluator().EvaluateAsync(Context(_world.OrgWide, [.. ids]), Ct);

        Assert.Equal((0, 60), (facts.Done, facts.Total));
        Assert.Equal(PartFacts.MaxMissingItems, facts.Missing.Count);
        Assert.Equal(50, PartFacts.MaxMissingItems);
        Assert.Equal(ids.Take(50), facts.Missing.Select(m => m.PropertyId));
    }

    /// <summary>
    /// The plan shows the part before the activation, with what is missing for each property, and the API says it: <c>missing</c> of the part carries the property, the
    /// reason and the step. For an organization with no Swiss property the part is not available and lists nothing; done, it lists nothing either.
    /// </summary>
    [Fact]
    public async Task Plan_ShowsThePartBeforeTheActivation_WithTheMissingItems_AndTheApiSaysThem()
    {
        var plans = Plans();

        var italian = await plans.GetPlanAsync(_world.OrgId, _world.OrgWide, Ct);
        var codes = italian.Parts.Select(p => p.Code).ToList();
        Assert.Equal(["guestRegistration", "activation"], codes.Skip(codes.Count - 2));
        Assert.Equal(12, codes.Count);
        var notAvailable = italian.Parts.Single(p => p.Key == OnboardingPartKey.GuestRegistration);
        Assert.Equal((OnboardingPartStates.NotAvailable, OnboardingPartReasons.NoSwissProperty, 0, 0), (notAvailable.State, notAvailable.Reason, notAvailable.Done, notAvailable.Total));
        Assert.Empty(notAvailable.Missing);

        await SwissAsync(_world.Granted, "6900", "Lugano");
        var open = (await plans.GetPlanAsync(_world.OrgId, _world.OrgWide, Ct)).Parts.Single(p => p.Key == OnboardingPartKey.GuestRegistration);
        Assert.Equal((OnboardingPartStates.Open, OnboardingPartReasons.TicinoStructureIdMissing, 0, 1), (open.State, open.Reason, open.Done, open.Total));
        Assert.Equal("guestRegistration", open.LaunchTarget);
        Assert.Empty(open.Requires);
        Assert.True(open.Deferrable);
        Assert.Empty(open.Needs);
        var dto = open.ToDto();
        var item = Assert.Single(dto.Missing);
        Assert.Equal((_world.Granted.Id, "ticino_structure_id_missing", "guest-registration"), (item.PropertyId, item.Reason, item.Code));

        await SetAsync(_world.Granted, p => p.TicinoStructureId = "NL-12345678");
        var done = (await plans.GetPlanAsync(_world.OrgId, _world.OrgWide, Ct)).Parts.Single(p => p.Key == OnboardingPartKey.GuestRegistration);
        Assert.Equal((OnboardingPartStates.Done, (string?)null, 1, 1), (done.State, done.Reason, done.Done, done.Total));
        Assert.Empty(done.Missing);
        Assert.Empty(done.ToDto().Missing);
    }

    /// <summary>The part is a twelfth key appended to the others, a plan of twelve in a fixed order, and nothing requires it (so it can be put off).</summary>
    [Fact]
    public void TheCatalog_HasTheTwelfthPart_BeforeTheActivation_AndNothingRequiresIt()
    {
        Assert.Equal(12, (int)OnboardingPartKey.GuestRegistration);
        Assert.Equal(11, (int)OnboardingPartKey.Activation);
        Assert.Equal(12, OnboardingPartCatalog.Parts.Count);
        Assert.Equal(Enumerable.Range(1, 12), OnboardingPartCatalog.Parts.Select(p => p.Order));
        Assert.Equal(OnboardingPartKey.Activation, OnboardingPartCatalog.Parts[^1].Key);
        Assert.Equal(OnboardingPartKey.GuestRegistration, OnboardingPartCatalog.Parts[^2].Key);
        Assert.Empty(OnboardingPartCatalog.RequiredBy(OnboardingPartKey.GuestRegistration));
        Assert.True(OnboardingPartCatalog.IsDeferrable(OnboardingPartKey.GuestRegistration));
        Assert.True(OnboardingPartCatalog.TryParse("guestRegistration", out var key));
        Assert.Equal(OnboardingPartKey.GuestRegistration, key);
        Assert.Equal(OnboardingPartKey.GuestRegistration, new GuestRegistrationPartEvaluator(_db, new PropertySwissPlaceResolver(_db)).Part);
        Assert.Equal(OnboardingPartEvaluatorOrders.Evaluator, new GuestRegistrationPartEvaluator(_db, new PropertySwissPlaceResolver(_db)).Order);
    }

    private GuestRegistrationPartEvaluator Evaluator() => new(_db, new PropertySwissPlaceResolver(_db));

    private OnboardingPlanService Plans()
    {
        var clock = new Casazen.Tests.Unit.FakeTimeProvider(HostScopeScenario.Now);
        IOnboardingPartEvaluator[] evaluators = [Evaluator()];
        return new OnboardingPlanService(
            _db,
            evaluators,
            new InternalActivityService(_db, NullLogger<InternalActivityService>.Instance, clock),
            clock,
            NullLogger<OnboardingPlanService>.Instance);
    }

    private static OnboardingPartContext Context(HostScope scope, params Guid[] propertyIds) => new(scope, propertyIds);

    private async Task SwissAsync(Property property, string postalCode, string city) =>
        await SetAsync(property, p =>
        {
            p.Country = "CH";
            p.PostalCode = postalCode;
            p.City = city;
        });

    private async Task SetAsync(Property property, Action<Property> change)
    {
        _db.ChangeTracker.Clear();
        var stored = await _db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == property.Id, Ct);
        change(stored);
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
    }
}
