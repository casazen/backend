using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class OnboardingBulkServiceTests : IAsyncLifetime
{
    private const string ValidCin = "IT058091C27G5FFZDZ";
    private const string Accommodation = "ACC-";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly SaveCounter _saves = new();
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private readonly Mock<IOctorateApiClient> _octorate = new();
    private AppDbContext _db = null!;
    private HostScopeWorld _world = null!;
    private Guid _connectionId;

    public async Task InitializeAsync()
    {
        _db = NewDb();
        _world = await HostScopeScenario.SeedAsync(_db);
        _octorate
            .Setup(o => o.GetPoliceAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctoratePoliceAccount());
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>
    /// Proof (permission and tenant isolation): a selection by id or by filter never reaches, previews or changes a property of another org, and a
    /// collaborator «Solo alcuni» never reaches the property it was not given. Such an id is the answer of an id that does not exist; no sender is
    /// written for it, no line of the audit is recorded about it.
    /// </summary>
    [Fact]
    public async Task OnboardingBulk_OtherOrgProperties_NeverSelectedOrChanged_Test()
    {
        await ConnectAsync();
        await ConfirmListingAsync(_world.Granted);
        await ConfirmListingAsync(_world.Hidden);
        var foreign = _world.OtherOrgProperty;
        await UpdateAsync(_world.Granted.Id, p => p.ComuneIstatCode = "058091");
        await UpdateAsync(foreign.Id, p => p.ComuneIstatCode = "058091");
        var bulk = Bulk();

        var byIds = await bulk.PreviewAsync(_world.OrgWide, Ids(_world.Granted.Id, foreign.Id), Ct);
        Assert.Equal((1, 1), (byIds.Total, byIds.NotFound));
        Assert.Equal([_world.Granted.Id], byIds.Sample.Select(s => s.PropertyId));

        var byFilter = await bulk.PreviewAsync(_world.OrgWide, Filter(comunes: ["058091"]), Ct);
        Assert.Equal([_world.Granted.Id], byFilter.Sample.Select(s => s.PropertyId));
        var all = await bulk.PreviewAsync(_world.OrgWide, All(), Ct);
        Assert.Equal(2, all.Total);
        Assert.DoesNotContain(foreign.Id, all.Sample.Select(s => s.PropertyId));

        var theirs = await bulk.PreviewAsync(new HostScope(_world.OtherOrgId), Ids(_world.Granted.Id), Ct);
        Assert.Equal((0, 1), (theirs.Total, theirs.NotFound));

        var result = await bulk.ApplyAsync(
            _world.OrgWide, _world.OwnerUserId, Ids(_world.Granted.Id, foreign.Id), Choice(ComplianceSenderType.Manual), false, true, Ct);
        Assert.Equal(BulkApplyOutcomes.Applied, Assert.Single(result.Items, i => i.PropertyId == _world.Granted.Id).Outcome);
        var theirItem = Assert.Single(result.Items, i => i.PropertyId == foreign.Id);
        Assert.Equal((BulkApplyOutcomes.Refused, OnboardingBulkCodes.PropertyNotFound), (theirItem.Outcome, theirItem.Reason));
        Assert.Null(theirItem.Name);
        Assert.False(await _db.PropertyComplianceSenders.AnyAsync(s => s.PropertyId == foreign.Id));
        Assert.DoesNotContain(
            await _db.OrgActivityEntries.AsNoTracking().Select(e => e.SubjectId).ToListAsync(),
            subject => subject == foreign.Id.ToString());

        Assert.Equal(1, (await bulk.PreviewAsync(_world.Restricted, All(), Ct)).Total);
        var restricted = await bulk.ApplyAsync(
            _world.Restricted, _world.CollaboratorId, Ids(_world.Hidden.Id), Choice(ComplianceSenderType.Manual), false, true, Ct);
        var hidden = Assert.Single(restricted.Items);
        Assert.Equal((BulkApplyOutcomes.Refused, OnboardingBulkCodes.PropertyNotFound), (hidden.Outcome, hidden.Reason));
        Assert.False(await _db.PropertyComplianceSenders.AnyAsync(s => s.PropertyId == _world.Hidden.Id));
    }

    [Fact]
    public async Task Selection_NeverMeansAllByAccident_AndIsCapped()
    {
        var bulk = Bulk();
        async Task<string> CodeOfAsync(BulkSelection selection) =>
            (await Assert.ThrowsAsync<DomainRuleException>(() => bulk.PreviewAsync(_world.OrgWide, selection, Ct))).Code;

        Assert.Equal(OnboardingActivationCodes.PropertyIdsEmpty, await CodeOfAsync(new BulkSelection(BulkSelectionKinds.Ids, [])));
        Assert.Equal(OnboardingActivationCodes.PropertyIdsEmpty, await CodeOfAsync(new BulkSelection(BulkSelectionKinds.Ids)));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(new BulkSelection(BulkSelectionKinds.Filter)));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(new BulkSelection(BulkSelectionKinds.Filter, Filter: new BulkFilter())));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(Filter(comunes: [])));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(Filter(regions: [])));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(Filter(channels: [])));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(Filter(comunes: ["58091"])));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(Filter(regions: ["XXX"])));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(Filter(channels: [" "])));
        Assert.Equal(
            OnboardingBulkCodes.SelectionInvalid,
            await CodeOfAsync(new BulkSelection(BulkSelectionKinds.Filter, Filter: new BulkFilter(FlowState: new BulkFlowStateFilter(OnboardingFlow.Bookings, "later")))));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(new BulkSelection(BulkSelectionKinds.All, [_world.Granted.Id])));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(new BulkSelection(BulkSelectionKinds.All, Filter: new BulkFilter(RegionCodes: ["LOM"]))));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(new BulkSelection(BulkSelectionKinds.Ids, [_world.Granted.Id], new BulkFilter(RegionCodes: ["LOM"]))));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(new BulkSelection(BulkSelectionKinds.Filter, [_world.Granted.Id], new BulkFilter(RegionCodes: ["LOM"]))));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(new BulkSelection("everything")));
        Assert.Equal(OnboardingBulkCodes.SelectionInvalid, await CodeOfAsync(new BulkSelection(string.Empty)));

        var small = Bulk(new OnboardingOptions { MaxPropertiesPerCall = 2 });
        var tooMany = await Assert.ThrowsAsync<DomainRuleException>(
            () => small.PreviewAsync(_world.OrgWide, Ids(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Ct));
        Assert.Equal(OnboardingActivationCodes.PropertyIdsTooMany, tooMany.Code);
        await AddPropertyAsync("Terza");
        var preview = await small.PreviewAsync(_world.OrgWide, All(), Ct);
        Assert.Equal((3, true, 2), (preview.Total, preview.ExceedsLimit, preview.MaxPropertiesPerCall));
        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => small.ApplyAsync(_world.OrgWide, _world.OwnerUserId, All(), Choice(ComplianceSenderType.Manual), false, true, Ct));
        Assert.Equal(OnboardingBulkCodes.SelectionTooLarge, refused.Code);
        Assert.False(await _db.PropertyComplianceSenders.AnyAsync());
    }

    [Fact]
    public async Task Selection_OnlyActiveShortRentProperties_SampleByName()
    {
        var longRent = await AddPropertyAsync("Lunga", p => p.RentalMode = RentalMode.Long);
        var off = await AddPropertyAsync("Spenta", p => p.IsActive = false);
        var deleted = await AddPropertyAsync("Cancellata", p => p.IsDeleted = true);
        var bulk = Bulk();

        var all = await bulk.PreviewAsync(_world.OrgWide, All(), Ct);
        Assert.Equal(["Casa Bianca", "Trullo"], all.Sample.Select(s => s.Name));
        Assert.Equal(2, all.Total);

        var named = await bulk.PreviewAsync(_world.OrgWide, Ids(longRent.Id, off.Id, deleted.Id, _world.Hidden.Id), Ct);
        Assert.Equal((1, 3), (named.Total, named.NotFound));

        for (var i = 0; i < 25; i++)
            await AddPropertyAsync($"Casa {i:00}");

        var many = await bulk.PreviewAsync(_world.OrgWide, All(), Ct);
        Assert.Equal((27, OnboardingBulkLimits.SampleSize), (many.Total, many.Sample.Count));
    }

    [Fact]
    public async Task Selection_Filters_ByComuneRegionChannelAndFlowState()
    {
        await ConnectAsync();
        var third = await AddPropertyAsync("Terza");
        await UpdateAsync(_world.Granted.Id, p => (p.ComuneIstatCode, p.RegionCode) = ("058091", "LAZ"));
        await UpdateAsync(_world.Hidden.Id, p => (p.ComuneIstatCode, p.RegionCode) = ("015146", "LOM"));
        await UpdateAsync(third.Id, p => (p.ComuneIstatCode, p.RegionCode) = ("058091", "LAZ"));
        AddChannel(_world.Granted, "booking_xml", ChannelPublishStatus.Published);
        AddChannel(_world.Hidden, "airbnb_xml", ChannelPublishStatus.Draft);
        await _db.SaveChangesAsync();
        await ConfirmListingAsync(_world.Granted);
        var bulk = Bulk();

        async Task<List<Guid>> SelectedAsync(BulkSelection selection) =>
            [.. (await bulk.PreviewAsync(_world.OrgWide, selection, Ct)).Sample.Select(s => s.PropertyId)];

        Assert.Equal([third.Id, _world.Granted.Id], await SelectedAsync(Filter(comunes: ["058091"])));
        Assert.Equal([_world.Hidden.Id, third.Id, _world.Granted.Id], await SelectedAsync(Filter(comunes: ["058091", "015146"])));
        Assert.Equal([_world.Hidden.Id], await SelectedAsync(Filter(regions: ["lom"])));
        Assert.Equal([_world.Hidden.Id], await SelectedAsync(Filter(channels: ["airbnb_xml"])));
        Assert.Equal([_world.Hidden.Id, _world.Granted.Id], await SelectedAsync(Filter(channels: ["airbnb_xml", "booking_xml"])));
        Assert.Equal([_world.Granted.Id], await SelectedAsync(Filter(comunes: ["058091"], channels: ["booking_xml"])));
        Assert.Empty(await SelectedAsync(Filter(comunes: ["058091"], regions: ["LOM"])));

        Assert.Equal(3, (await bulk.PreviewAsync(_world.OrgWide, Filter(missingFlow: OnboardingFlow.Bookings), Ct)).Total);
        Assert.Equal(
            [_world.Hidden.Id, third.Id, _world.Granted.Id],
            await SelectedAsync(Filter(comunes: ["015146", "058091"], flowState: new BulkFlowStateFilter(OnboardingFlow.Bookings, "toComplete"))));

        await Activation().ActivateAsync(_world.OrgWide, _world.OwnerUserId, OnboardingFlow.Bookings, [_world.Granted.Id], Ct);
        Assert.Equal([_world.Hidden.Id, third.Id], await SelectedAsync(Filter(missingFlow: OnboardingFlow.Bookings)));
        Assert.Equal([_world.Granted.Id], await SelectedAsync(Filter(flowState: new BulkFlowStateFilter(OnboardingFlow.Bookings, "active"))));
        Assert.Equal(3, (await bulk.PreviewAsync(_world.OrgWide, Filter(flowState: new BulkFlowStateFilter(OnboardingFlow.Messages, "notAvailable")), Ct)).Total);
    }

    /// <summary>
    /// A dry run, and an apply without the confirmation, write nothing at all: no save, no sender row (not even for a property that only has the
    /// default), no consent, no audit line, no call to Octorate. Each property is judged by the rules of the switch of E03 and reported
    /// <c>wouldChange</c>, <c>unchanged</c> or <c>refused</c> with the rule.
    /// </summary>
    /// <summary>
    /// E20/F01/US01/T01 (decision O1): the org-wide setting of the guests written to the channel manager on their own rides on the confirmed apply, once for
    /// the org and never for each property; a dry run and an apply that is not confirmed write nothing, and the answer says what was asked.
    /// </summary>
    [Fact]
    public async Task Apply_TheOrgWideAutoSendRidesOnTheConfirmedApply_AndNeverOnALook()
    {
        await ConnectAsync();
        await ConfirmListingAsync(_world.Granted);
        var bulk = Bulk();
        var choice = new BulkChoice(ComplianceSenderType.Manual, AutoSendGuestsToOctorate: true);

        async Task<bool> StoredAsync() => (await _db.Orgs.AsNoTracking().SingleAsync(o => o.Id == _world.OrgId)).AutoSendGuestsToOctorate;

        var look = await bulk.ApplyAsync(_world.OrgWide, _world.OwnerUserId, All(), choice, dryRun: true, confirmed: true, Ct);
        var unconfirmed = await bulk.ApplyAsync(_world.OrgWide, _world.OwnerUserId, All(), choice, dryRun: false, confirmed: false, Ct);
        Assert.All([look, unconfirmed], r => Assert.Equal((true, true), (r.DryRun, r.AutoSendGuestsToOctorate)));
        Assert.False(await StoredAsync());

        var applied = await bulk.ApplyAsync(_world.OrgWide, _world.OwnerUserId, All(), choice, dryRun: false, confirmed: true, Ct);

        Assert.Equal((false, true), (applied.DryRun, applied.AutoSendGuestsToOctorate));
        Assert.True(await StoredAsync());
        var org = await _db.Orgs.AsNoTracking().SingleAsync(o => o.Id == _world.OrgId);
        Assert.Equal(_world.OwnerUserId, org.AutoSendGuestsToOctorateChangedByUserId);

        var sender = await bulk.ApplyAsync(_world.OrgWide, _world.OwnerUserId, All(), Choice(ComplianceSenderType.Manual), dryRun: false, confirmed: true, Ct);
        Assert.Null(sender.AutoSendGuestsToOctorate);
        Assert.True(await StoredAsync());
    }

    [Fact]
    public async Task Apply_DryRunOrNotConfirmed_WritesNothing_AndReportsTheRuleOfEachProperty()
    {
        await ConnectAsync();
        await ConfirmListingAsync(_world.Granted);
        await ConfirmListingAsync(_world.Hidden);
        var plain = await AddPropertyAsync("Senza canale");
        var swiss = await AddPropertyAsync("Lugano", p => (p.Country, p.Currency) = ("CH", "CHF"));
        var bulk = Bulk();
        var everyone = All();
        var saves = _saves.Count;

        async Task<Dictionary<string, BulkApplyItem>> JudgeAsync(ComplianceSenderType sender, bool dryRun, bool confirmed)
        {
            var result = await bulk.ApplyAsync(_world.OrgWide, _world.OwnerUserId, everyone, Choice(sender), dryRun, confirmed, Ct);
            Assert.True(result.DryRun);
            return result.Items.ToDictionary(item => item.Name!);
        }

        var manual = await JudgeAsync(ComplianceSenderType.Manual, dryRun: true, confirmed: true);
        Assert.Equal(BulkApplyOutcomes.WouldChange, manual["Trullo"].Outcome);
        Assert.Equal(BulkApplyOutcomes.WouldChange, manual["Casa Bianca"].Outcome);
        Assert.Equal(BulkApplyOutcomes.Unchanged, manual[plain.Name].Outcome);
        Assert.Equal((BulkApplyOutcomes.Refused, "sender_not_applicable"), (manual[swiss.Name].Outcome, manual[swiss.Name].Reason));

        var unconfirmed = await JudgeAsync(ComplianceSenderType.Manual, dryRun: false, confirmed: false);
        Assert.Equal(manual.Select(m => (m.Key, m.Value.Outcome)), unconfirmed.Select(m => (m.Key, m.Value.Outcome)));

        var casaZen = await JudgeAsync(ComplianceSenderType.CasaZen, dryRun: true, confirmed: true);
        Assert.All(casaZen.Values.Where(i => i.Name != swiss.Name), i => Assert.Equal("sender_casazen_not_available", i.Reason));
        var withoutDelegation = await JudgeAsync(ComplianceSenderType.ChannelManager, dryRun: true, confirmed: true);
        Assert.Equal("alloggiati_delegation_required", withoutDelegation["Trullo"].Reason);
        Assert.Equal("sender_channel_manager_not_available", withoutDelegation[plain.Name].Reason);

        Assert.Equal(saves, _saves.Count);
        Assert.Empty(await _db.PropertyComplianceSenders.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());
        Assert.Null((await _db.Orgs.AsNoTracking().SingleAsync(o => o.Id == _world.OrgId)).AlloggiatiDelegationGrantedAt);
        _octorate.Verify(o => o.GetPoliceAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        await GrantDelegationAsync();
        var saved = _saves.Count;
        var channelManager = await JudgeAsync(ComplianceSenderType.ChannelManager, dryRun: true, confirmed: true);
        Assert.Equal(BulkApplyOutcomes.Unchanged, channelManager["Trullo"].Outcome);
        Assert.Equal(BulkApplyOutcomes.Unchanged, channelManager["Casa Bianca"].Outcome);
        Assert.Equal("sender_channel_manager_not_available", channelManager[plain.Name].Reason);
        Assert.Equal(saved, _saves.Count);
        Assert.Empty(await _db.PropertyComplianceSenders.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Confirmed, each property goes through the sender switch: the sender is written for both communications, the audit records the real changes
    /// (once each), a repeat is <c>unchanged</c>, and the consent to the delegation is never given in bulk: an automatic sender is refused until the
    /// org has accepted it, and then switched.
    /// </summary>
    [Fact]
    public async Task Apply_Confirmed_SwitchesEachPropertyThroughTheSenderSwitch_AndNeverGrantsTheDelegation()
    {
        await ConnectAsync();
        await ConfirmListingAsync(_world.Granted);
        await ConfirmListingAsync(_world.Hidden);
        var plain = await AddPropertyAsync("Senza canale");
        var bulk = Bulk();

        var manual = await bulk.ApplyAsync(
            _world.OrgWide, _world.OwnerUserId, All(), Choice(ComplianceSenderType.Manual), false, true, Ct);
        Assert.False(manual.DryRun);
        Assert.Equal((2, 1, 0, 0), (manual.Summary.Applied, manual.Summary.Unchanged, manual.Summary.Refused, manual.Summary.WouldChange));
        var rows = await _db.PropertyComplianceSenders.AsNoTracking().ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal((ComplianceSenderType.Manual, ComplianceSenderType.Manual), (row.AlloggiatiSender, row.IstatSender)));
        Assert.Equal(_world.OwnerUserId, rows[0].LastChangedByUserId);

        var lines = await _db.OrgActivityEntries.AsNoTracking().Where(e => e.Type == OrgActivityType.PropertySenderChanged).ToListAsync();
        Assert.Equal(
            new[] { _world.Granted.Id.ToString(), _world.Hidden.Id.ToString() }.Order(),
            lines.Select(line => line.SubjectId).Order());

        var again = await bulk.ApplyAsync(
            _world.OrgWide, _world.OwnerUserId, All(), Choice(ComplianceSenderType.Manual), false, true, Ct);
        Assert.Equal((0, 3, 0), (again.Summary.Applied, again.Summary.Unchanged, again.Summary.Refused));
        Assert.Equal(2, await _db.OrgActivityEntries.CountAsync(e => e.Type == OrgActivityType.PropertySenderChanged));

        var refused = await bulk.ApplyAsync(
            _world.OrgWide, _world.OwnerUserId, All(), Choice(ComplianceSenderType.ChannelManager), false, true, Ct);
        Assert.Equal("alloggiati_delegation_required", refused.Items.First(i => i.Name == "Trullo").Reason);
        Assert.Equal("sender_channel_manager_not_available", refused.Items.First(i => i.Name == plain.Name).Reason);
        Assert.Equal(3, refused.Summary.Refused);
        Assert.Null((await _db.Orgs.AsNoTracking().SingleAsync(o => o.Id == _world.OrgId)).AlloggiatiDelegationGrantedAt);

        await GrantDelegationAsync();
        var channelManager = await bulk.ApplyAsync(
            _world.OrgWide, _world.OwnerUserId, All(), Choice(ComplianceSenderType.ChannelManager), false, true, Ct);
        Assert.Equal((2, 0, 1), (channelManager.Summary.Applied, channelManager.Summary.Unchanged, channelManager.Summary.Refused));
        Assert.All(
            await _db.PropertyComplianceSenders.AsNoTracking().Where(s => s.PropertyId != plain.Id).ToListAsync(),
            row => Assert.Equal(ComplianceSenderType.ChannelManager, row.AlloggiatiSender));
        Assert.Equal(ComplianceSenderType.Manual, (await _db.PropertyComplianceSenders.AsNoTracking().SingleAsync(s => s.PropertyId == plain.Id)).AlloggiatiSender);

        // The answer of a bulk call is the outcome of each property, so the view of a switched property is not built: the read of the police account
        // on Octorate that builds it (up to 8 seconds for each property, one after the other in one request) is not made, not even for the two
        // properties that were switched to the channel manager.
        _octorate.Verify(o => o.GetPoliceAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        var missing = await Assert.ThrowsAsync<DomainRuleException>(
            () => bulk.ApplyAsync(_world.OrgWide, _world.OwnerUserId, All(), new BulkChoice(null), false, true, Ct));
        Assert.Equal(OnboardingBulkCodes.ChoiceInvalid, missing.Code);
        var unknown = await Assert.ThrowsAsync<DomainRuleException>(
            () => bulk.ApplyAsync(_world.OrgWide, _world.OwnerUserId, All(), new BulkChoice((ComplianceSenderType)99), false, true, Ct));
        Assert.Equal(OnboardingBulkCodes.ChoiceInvalid, unknown.Code);
    }

    [Fact]
    public async Task Progress_CountsTheReadyPropertiesOfTheTotal_AndEachFlow()
    {
        await ConnectAsync();
        var third = await AddPropertyAsync("Terza");
        await AddPropertyAsync("Lunga", p => p.RentalMode = RentalMode.Long);
        await AddPropertyAsync("Spenta", p => p.IsActive = false);
        await MakeReadyAsync(_world.Granted);

        var progress = await Bulk().GetProgressAsync(_world.OrgWide, Ct);
        Assert.Equal((3, 1, 2), (progress.Total, progress.Ready, progress.NotReady));
        Assert.Equal(Enum.GetValues<OnboardingFlow>().OrderBy(f => (int)f), progress.Flows.Select(f => f.Flow));
        Assert.All(progress.Flows, flow => Assert.Equal(3, flow.Active + flow.Ready + flow.ToComplete + flow.NotAvailable));
        var bookings = progress.Flows.Single(f => f.Flow == OnboardingFlow.Bookings);
        Assert.Equal((0, 1, 2, 0), (bookings.Active, bookings.Ready, bookings.ToComplete, bookings.NotAvailable));
        var messages = progress.Flows.Single(f => f.Flow == OnboardingFlow.Messages);
        Assert.Equal((0, 0, 0, 3), (messages.Active, messages.Ready, messages.ToComplete, messages.NotAvailable));

        await Activation().ActivateAsync(_world.OrgWide, _world.OwnerUserId, OnboardingFlow.Bookings, [_world.Granted.Id], Ct);
        var activated = await Bulk().GetProgressAsync(_world.OrgWide, Ct);
        Assert.Equal((3, 1), (activated.Total, activated.Ready));
        Assert.Equal((1, 0, 2), (Flow(activated, OnboardingFlow.Bookings).Active, Flow(activated, OnboardingFlow.Bookings).Ready, Flow(activated, OnboardingFlow.Bookings).ToComplete));

        await MakeReadyAsync(_world.Hidden);
        await MakeReadyAsync(third);
        var allReady = await Bulk().GetProgressAsync(_world.OrgWide, Ct);
        Assert.Equal((3, 3, 0), (allReady.Total, allReady.Ready, allReady.NotReady));

        var restricted = await Bulk().GetProgressAsync(_world.Restricted, Ct);
        Assert.Equal((1, 1), (restricted.Total, restricted.Ready));
        var other = await Bulk().GetProgressAsync(new HostScope(_world.OtherOrgId), Ct);
        Assert.Equal((1, 0), (other.Total, other.Ready));
    }

    /// <summary>
    /// The pilot group (H10): 1 to the configured maximum distinct properties of the caller that the flows apply to, replaced in one save; more is
    /// 422, fewer than five is accepted and reported, an empty list is 422 (never «no pilot group»), and an id that is not the caller's is a 404 with
    /// nothing written. A collaborator «Solo alcuni» sees and replaces the members it reaches.
    /// </summary>
    [Fact]
    public async Task Pilot_SetReplacesTheGroup_WithItsLimits_AndOnlyWithThePropertiesOfTheCaller()
    {
        var third = await AddPropertyAsync("Terza");
        var longRent = await AddPropertyAsync("Lunga", p => p.RentalMode = RentalMode.Long);
        var bulk = Bulk(new OnboardingOptions { PilotGroupMaxSize = 2 });

        Assert.Empty((await bulk.GetPilotAsync(_world.OrgWide, Ct)).Members);

        var empty = await Assert.ThrowsAsync<DomainRuleException>(() => bulk.SetPilotAsync(_world.OrgWide, _world.OwnerUserId, [], Ct));
        Assert.Equal(OnboardingActivationCodes.PropertyIdsEmpty, empty.Code);
        var large = await Assert.ThrowsAsync<DomainRuleException>(
            () => bulk.SetPilotAsync(_world.OrgWide, _world.OwnerUserId, [_world.Granted.Id, _world.Hidden.Id, third.Id], Ct));
        Assert.Equal(OnboardingBulkCodes.PilotGroupTooLarge, large.Code);

        foreach (var notMine in new[] { _world.OtherOrgProperty.Id, longRent.Id, Guid.NewGuid() })
        {
            var refused = await Assert.ThrowsAsync<NotFoundException>(
                () => bulk.SetPilotAsync(_world.OrgWide, _world.OwnerUserId, [_world.Granted.Id, notMine], Ct));
            Assert.Equal(OnboardingActivationCodes.PropertyNotFound, refused.Code);
        }

        Assert.Empty(await _db.OnboardingPilotMembers.AsNoTracking().ToListAsync());

        var first = await bulk.SetPilotAsync(_world.OrgWide, _world.OwnerUserId, [_world.Hidden.Id, _world.Granted.Id, _world.Granted.Id], Ct);
        Assert.Equal((2, true, OnboardingBulkLimits.PilotRecommendedMin, 2), (first.Size, first.BelowRecommended, first.RecommendedMin, first.MaxSize));
        Assert.Equal(["Casa Bianca", "Trullo"], first.Members.Select(m => m.Name));
        var addedAt = first.Members.Single(m => m.PropertyId == _world.Hidden.Id).AddedAt;
        Assert.Equal(HostScopeScenario.Now.UtcDateTime, addedAt);
        Assert.Equal(_world.OwnerUserId, (await _db.OnboardingPilotMembers.AsNoTracking().FirstAsync()).AddedByUserId);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await bulk.SetPilotAsync(_world.OrgWide, _world.OwnerUserId, [_world.Hidden.Id, third.Id], Ct);
        Assert.Equal(["Casa Bianca", "Terza"], second.Members.Select(m => m.Name));
        Assert.Equal(addedAt, second.Members.Single(m => m.PropertyId == _world.Hidden.Id).AddedAt);
        Assert.Equal(2, await _db.OnboardingPilotMembers.CountAsync());

        var saves = _saves.Count;
        await bulk.SetPilotAsync(_world.OrgWide, _world.OwnerUserId, [third.Id, _world.Hidden.Id], Ct);
        Assert.Equal(saves, _saves.Count);

        Assert.Empty((await bulk.GetPilotAsync(_world.Restricted, Ct)).Members);
        var hidden = await Assert.ThrowsAsync<NotFoundException>(
            () => bulk.SetPilotAsync(_world.Restricted, _world.CollaboratorId, [_world.Hidden.Id], Ct));
        Assert.Equal(OnboardingActivationCodes.PropertyNotFound, hidden.Code);
        var granted = await bulk.SetPilotAsync(_world.Restricted, _world.CollaboratorId, [_world.Granted.Id], Ct);
        Assert.Equal([_world.Granted.Id], granted.Members.Select(m => m.PropertyId));
        Assert.Equal(
            new[] { _world.Granted.Id, _world.Hidden.Id, third.Id }.Order(),
            (await _db.OnboardingPilotMembers.AsNoTracking().Select(m => m.PropertyId).ToListAsync()).Order());
    }

    [Fact]
    public async Task Activate_PilotFirst_ThenTheRest_WhichWaitForThePilotToBeActive()
    {
        await ConnectAsync();
        var third = await AddPropertyAsync("Terza");
        await ConfirmListingAsync(_world.Granted);
        await ConfirmListingAsync(_world.Hidden);
        var bulk = Bulk(new OnboardingOptions { PilotGroupMaxSize = 1 });
        var bookings = new[] { OnboardingFlow.Bookings };

        var noPilot = await Assert.ThrowsAsync<DomainRuleException>(
            () => bulk.ActivateAsync(_world.OrgWide, _world.OwnerUserId, BulkCohorts.Pilot, bookings, Ct));
        Assert.Equal(OnboardingBulkCodes.PilotGroupEmpty, noPilot.Code);
        var waits = await Assert.ThrowsAsync<DomainConflictException>(
            () => bulk.ActivateAsync(_world.OrgWide, _world.OwnerUserId, BulkCohorts.Rest, bookings, Ct));
        Assert.Equal(OnboardingBulkCodes.PilotGroupNotActive, waits.Code);

        var cohort = await Assert.ThrowsAsync<DomainRuleException>(
            () => bulk.ActivateAsync(_world.OrgWide, _world.OwnerUserId, "everyone", bookings, Ct));
        Assert.Equal(OnboardingBulkCodes.CohortInvalid, cohort.Code);
        var noFlows = await Assert.ThrowsAsync<DomainRuleException>(
            () => bulk.ActivateAsync(_world.OrgWide, _world.OwnerUserId, BulkCohorts.Pilot, [], Ct));
        Assert.Equal(OnboardingBulkCodes.FlowsEmpty, noFlows.Code);
        Assert.Empty(await _db.PropertyFlowActivations.AsNoTracking().ToListAsync());

        await bulk.SetPilotAsync(_world.OrgWide, _world.OwnerUserId, [_world.Granted.Id], Ct);
        await Assert.ThrowsAsync<DomainConflictException>(
            () => bulk.ActivateAsync(_world.OrgWide, _world.OwnerUserId, BulkCohorts.Rest, bookings, Ct));
        Assert.Empty(await _db.PropertyFlowActivations.AsNoTracking().ToListAsync());

        var pilot = await bulk.ActivateAsync(_world.OrgWide, _world.OwnerUserId, BulkCohorts.Pilot, bookings, Ct);
        var pilotOutcome = Assert.Single(Assert.Single(pilot.Flows).Outcomes);
        Assert.Equal((_world.Granted.Id, FlowActivationOutcomes.Activated), (pilotOutcome.PropertyId, pilotOutcome.Outcome));
        Assert.Equal([_world.Granted.Id], (await _db.PropertyFlowActivations.AsNoTracking().Select(a => a.PropertyId).ToListAsync()));
        var state = await bulk.GetPilotAsync(_world.OrgWide, Ct);
        Assert.Contains(state.Flows, f => f.Flow == OnboardingFlow.Bookings && f.IsActive && f is { Available: 1, Active: 1 });
        Assert.DoesNotContain(state.Flows, f => f.Flow == OnboardingFlow.Listings && f.IsActive);

        var rest = await bulk.ActivateAsync(_world.OrgWide, _world.OwnerUserId, BulkCohorts.Rest, bookings, Ct);
        var outcomes = Assert.Single(rest.Flows).Outcomes;
        Assert.Equal([_world.Hidden.Id, third.Id], outcomes.Select(o => o.PropertyId));
        Assert.Equal(FlowActivationOutcomes.Activated, outcomes[0].Outcome);
        Assert.Equal(FlowActivationOutcomes.PrerequisitesNotMet, outcomes[1].Outcome);
        Assert.Equal([OnboardingPrerequisiteKeys.Mapping], outcomes[1].UnmetPrerequisites);
        Assert.Equal(
            new[] { _world.Granted.Id, _world.Hidden.Id }.Order(),
            (await _db.PropertyFlowActivations.AsNoTracking().Select(a => a.PropertyId).ToListAsync()).Order());
        Assert.Equal(2, await _db.OutboxEvents.CountAsync(e => e.EventType == DomainEventType.OnboardingFlowActivated));

        var listings = await Assert.ThrowsAsync<DomainConflictException>(
            () => bulk.ActivateAsync(_world.OrgWide, _world.OwnerUserId, BulkCohorts.Rest, [OnboardingFlow.Bookings, OnboardingFlow.Listings], Ct));
        Assert.Equal(OnboardingBulkCodes.PilotGroupNotActive, listings.Code);
    }

    [Fact]
    public async Task Activate_RestOfASmallOrg_NeedsNoPilotGroup()
    {
        await ConnectAsync();
        await ConfirmListingAsync(_world.Granted);
        await ConfirmListingAsync(_world.Hidden);
        var bulk = Bulk();

        var rest = await bulk.ActivateAsync(_world.OrgWide, _world.OwnerUserId, BulkCohorts.Rest, [OnboardingFlow.Bookings], Ct);

        Assert.Equal(BulkCohorts.Rest, rest.Cohort);
        var outcomes = Assert.Single(rest.Flows).Outcomes;
        Assert.All(outcomes, o => Assert.Equal(FlowActivationOutcomes.Activated, o.Outcome));
        Assert.Equal(2, outcomes.Count);
        Assert.Equal(2, await _db.PropertyFlowActivations.CountAsync());

        _db.PropertyFlowActivations.RemoveRange(await _db.PropertyFlowActivations.ToListAsync());
        await _db.SaveChangesAsync();
        var restricted = await bulk.ActivateAsync(_world.Restricted, _world.CollaboratorId, BulkCohorts.Rest, [OnboardingFlow.Bookings], Ct);
        Assert.Equal([_world.Granted.Id], Assert.Single(restricted.Flows).Outcomes.Select(o => o.PropertyId));
        Assert.Equal([_world.Granted.Id], await _db.PropertyFlowActivations.AsNoTracking().Select(a => a.PropertyId).ToListAsync());
    }

    [Fact]
    public void Registration_TheBulkServices_AndTheOptions()
    {
        var services = new ServiceCollection();
        services.AddCasazenOnboarding(new ConfigurationBuilder().Build());

        Assert.Equal(typeof(OnboardingBulkService), Assert.Single(services, d => d.ServiceType == typeof(IOnboardingBulkService)).ImplementationType);
        Assert.Equal(typeof(OnboardingBulkImportService), Assert.Single(services, d => d.ServiceType == typeof(IOnboardingBulkImportService)).ImplementationType);
        Assert.All(
            services.Where(d => d.ServiceType == typeof(IOnboardingBulkService) || d.ServiceType == typeof(IOnboardingBulkImportService)),
            d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));

        var bound = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Onboarding:PilotGroupMaxSize"] = "7" })
            .Build();
        var options = new OnboardingOptions();
        bound.GetSection(OnboardingOptions.SectionName).Bind(options);
        Assert.Equal((7, 500), (options.EffectivePilotGroupMaxSize, options.EffectiveMaxPropertiesPerCall));
        Assert.Equal((10, 500), (new OnboardingOptions().EffectivePilotGroupMaxSize, new OnboardingOptions().EffectiveMaxPropertiesPerCall));
        Assert.Equal(1, new OnboardingOptions { PilotGroupMaxSize = 0 }.EffectivePilotGroupMaxSize);
        Assert.Equal(500, new OnboardingOptions { PilotGroupMaxSize = 5000 }.EffectivePilotGroupMaxSize);
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).AddInterceptors(_saves).Options);

    private OnboardingBulkService Bulk(OnboardingOptions? options = null)
    {
        var (sender, store) = Senders();
        return new OnboardingBulkService(
            _db,
            FlowStatus(),
            Activation(),
            sender,
            store,
            Options.Create(options ?? new OnboardingOptions()),
            _clock,
            NullLogger<OnboardingBulkService>.Instance);
    }

    private OnboardingFlowActivationService Activation() =>
        new(_db, FlowStatus(), new DomainEventRecorder(_db, NullTenantContext.Instance, _clock), _clock, NullLogger<OnboardingFlowActivationService>.Instance);

    private (PropertySenderService Service, PropertySenderStore Store) Senders()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(h => h.EnvironmentName).Returns("Staging");
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        var scope = new Mock<IOctorateOrgScope>();
        scope.Setup(s => s.Use(It.IsAny<Guid>())).Returns(Mock.Of<IDisposable>());
        var questura = new Mock<IQuesturaCredentialsService>();
        questura.Setup(q => q.GetStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(QuesturaCredentialsStatus.NotConfigured);
        var activity = new ActivityLog(_db, _clock);
        var store = new PropertySenderStore(_db, activity, environment.Object, _clock);
        var service = new PropertySenderService(
            _db,
            store,
            new AlloggiatiDelegationService(_db, activity, _clock),
            questura.Object,
            _octorate.Object,
            scope.Object,
            flags.Object,
            environment.Object,
            NullLogger<PropertySenderService>.Instance,
            _clock);
        return (service, store);
    }

    private OnboardingFlowStatusService FlowStatus()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Staging");
        var reader = new FlowPrerequisiteReader(_db, new DatabaseMappingReader(_db), environment.Object, NullLogger<FlowPrerequisiteReader>.Instance);
        var touristTax = new Mock<ITouristTaxQuoteService>();
        touristTax
            .Setup(t => t.GetRatesInForceAsync(It.IsAny<TouristTaxComune>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TouristTaxRate>)[new TouristTaxRate { City = "Ostuni", RatePerPersonPerNight = 2m }]);
        IFlowReadinessProvider[] providers =
        [
            new ListingsFlowProvider(_db, reader),
            new BookingsFlowProvider(reader),
            new CheckInComplianceFlowProvider(_db, reader, touristTax.Object, _clock),
            new NotProvidedFlowProvider(OnboardingFlow.Messages),
            new NotProvidedFlowProvider(OnboardingFlow.Documents),
            new NotProvidedFlowProvider(OnboardingFlow.Reports),
        ];
        return new OnboardingFlowStatusService(_db, providers);
    }

    private async Task ConnectAsync()
    {
        var connection = new OctorateConnection { OrgId = _world.OrgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
        _db.OctorateConnections.Add(connection);
        await _db.SaveChangesAsync();
        _connectionId = connection.Id;
    }

    private async Task ConfirmListingAsync(Property property)
    {
        _db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = _world.OrgId,
            ConnectionId = _connectionId,
            OctorateAccommodationId = Accommodation + property.Id.ToString("N"),
            PropertyId = property.Id,
            Status = OctorateMappingStatus.Confirmed,
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>Every prerequisite of the three flows that exist: the listing, a stored single sender and a valid CIN.</summary>
    private async Task MakeReadyAsync(Property property)
    {
        await ConfirmListingAsync(property);
        await UpdateAsync(property.Id, p => p.CinCode = ValidCin);
        var (_, store) = Senders();
        await store.SetAsync(property.Id, ComplianceSenderType.Manual, _world.OwnerUserId, Ct);
    }

    private async Task GrantDelegationAsync()
    {
        var activity = new ActivityLog(_db, _clock);
        await new AlloggiatiDelegationService(_db, activity, _clock)
            .GrantAsync(_world.OrgId, _world.OwnerUserId, AlloggiatiDelegationTexts.CurrentVersion, Ct);
    }

    private async Task<Property> AddPropertyAsync(string name, Action<Property>? configure = null)
    {
        var property = HostScopeScenario.NewProperty(_world.OrgId, _world.OwnerUserId, name);
        configure?.Invoke(property);
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();
        return property;
    }

    private async Task UpdateAsync(Guid propertyId, Action<Property> change)
    {
        var property = await _db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId);
        change(property);
        await _db.SaveChangesAsync();
    }

    private void AddChannel(Property property, string code, ChannelPublishStatus status) =>
        _db.ListingChannelStates.Add(new ListingChannelState { OrgId = property.OrgId, PropertyId = property.Id, ChannelCode = code, Status = status });

    private static BulkSelection All() => new(BulkSelectionKinds.All);

    private static BulkSelection Ids(params Guid[] ids) => new(BulkSelectionKinds.Ids, ids);

    private static BulkSelection Filter(
        string[]? comunes = null,
        string[]? regions = null,
        string[]? channels = null,
        OnboardingFlow? missingFlow = null,
        BulkFlowStateFilter? flowState = null) =>
        new(BulkSelectionKinds.Filter, Filter: new BulkFilter(comunes, regions, channels, missingFlow, flowState));

    private static BulkChoice Choice(ComplianceSenderType sender) => new(sender);

    private static BulkFlowProgress Flow(BulkProgress progress, OnboardingFlow flow) => progress.Flows.Single(f => f.Flow == flow);

    private sealed class DatabaseMappingReader(AppDbContext db) : IPropertyMappingReader
    {
        public bool IsProvided => true;

        public async Task<IReadOnlyDictionary<Guid, string>> GetConfirmedAccommodationIdsAsync(
            Guid orgId,
            IReadOnlyCollection<Guid> propertyIds,
            CancellationToken ct) =>
            await db.OctorateListingMappings
                .AsNoTracking()
                .Where(m => m.OrgId == orgId && m.Status == OctorateMappingStatus.Confirmed && m.PropertyId != null && propertyIds.Contains(m.PropertyId.Value))
                .ToDictionaryAsync(m => m.PropertyId!.Value, m => m.OctorateAccommodationId, ct);
    }

    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Count++;
            return base.SavingChanges(eventData, result);
        }
    }
}
