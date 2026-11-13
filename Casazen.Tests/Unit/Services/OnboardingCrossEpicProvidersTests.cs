using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Casazen.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The readiness providers and the part evaluators of the other epics (E10/F01/US04/T01, NF-106, NF-104): the messages, documents and reports flows
/// and the parts «Messages and AI», «Fiscal documents» and «Partners», on EF InMemory and the world of <see cref="HostScopeScenario"/> (an org with a
/// property given to the collaborator «Solo alcuni», one not given, and another org). Every state is read from the facts the epics store; nothing here
/// is a flag that says «ready».
/// </summary>
public class OnboardingCrossEpicProvidersTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly OnboardingFlow[] CrossEpicFlows = [OnboardingFlow.Messages, OnboardingFlow.Documents, OnboardingFlow.Reports];

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly Mock<IFeatureFlags> _flags = new();
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ---------------------------------------------------------------- the flows

    /// <summary>
    /// Proof (data, NF-106, AC-NF106-1, AC-NF106-4): the state of the messages, documents and reports flows follows the stored facts. With nothing but
    /// the host's activation stored no flow is active and none says «not provided»; each says what is missing and what fixes it. The facts arrive and
    /// the three flows run; they are taken away again (a license without the chat module, a choice of the accountant, a cancelled booking) and the flows
    /// follow them although the activation rows are still there: a missing issuer, a <c>chat_not_licensed</c> error and no imported booking never
    /// leave a state <c>active</c>.
    /// </summary>
    [Fact]
    public async Task OnboardingCrossEpicProviders_StateComesFromStoredFacts_Test()
    {
        await ConnectAsync();
        var status = FlowStatus(Reader(_world.Granted.Id));
        _db.PropertyFlowActivations.AddRange(CrossEpicFlows.Select(flow => Activation(_world.Granted.Id, flow)));
        await _db.SaveChangesAsync();

        // Nothing stored but the host's activation.
        var missing = await StatusesAsync(status);
        Assert.Equal(
            (OnboardingFlowStates.ToComplete, (string?)OnboardingFlowReasons.MessageSyncNotReady, (string?)null),
            Of(missing, OnboardingFlow.Messages));
        Assert.Equal(
            (OnboardingFlowStates.ToComplete, (string?)OnboardingFlowReasons.IssuerMissing, (string?)OnboardingFlowActions.ChooseIssuer),
            Of(missing, OnboardingFlow.Documents));
        Assert.Equal(
            (OnboardingFlowStates.ToComplete, (string?)OnboardingFlowReasons.NoBookingsYet, (string?)null),
            Of(missing, OnboardingFlow.Reports));
        Assert.DoesNotContain(missing.Values, s => s.State == OnboardingFlowStates.Active || s.Reason == OnboardingFlowReasons.NotProvided);

        // The facts arrive: a pass of the message sync read the listing, the host chose Octorate as the issuer, a booking came from the channel manager.
        _db.OtaMessageSyncStates.Add(SyncState(_world.Granted.Id));
        _db.OrgFiscalIssuerProfiles.Add(Issuer(DocumentIssuerMode.Octorate));
        await _db.SaveChangesAsync();
        await AddBookingAsync(_world.Granted, BookingStatus.Confirmed);
        var ready = await StatusesAsync(status);
        Assert.All(CrossEpicFlows, flow => Assert.Equal(OnboardingFlowStates.Active, ready[flow].State));

        // The facts go: the flows follow them, the host's activation is still stored.
        (await _db.OtaMessageSyncStates.SingleAsync()).LastError = OtaMessageSyncErrorCodes.ChatNotLicensed;
        (await _db.OrgFiscalIssuerProfiles.SingleAsync()).IssuerMode = DocumentIssuerMode.Accountant;
        (await _db.Bookings.SingleAsync(b => b.OctorateReservationId != null)).Status = BookingStatus.Cancelled;
        await _db.SaveChangesAsync();
        var gone = await StatusesAsync(status);

        Assert.Equal(
            (OnboardingFlowStates.NotAvailable, (string?)OnboardingFlowReasons.ChatNotLicensed, (string?)null),
            Of(gone, OnboardingFlow.Messages));
        Assert.Equal(
            (OnboardingFlowStates.NotAvailable, (string?)OnboardingFlowReasons.AccountantHandles, (string?)null),
            Of(gone, OnboardingFlow.Documents));
        Assert.Equal(
            (OnboardingFlowStates.ToComplete, (string?)OnboardingFlowReasons.NoBookingsYet, (string?)null),
            Of(gone, OnboardingFlow.Reports));
        Assert.Equal(3, await _db.PropertyFlowActivations.CountAsync(a => a.DeactivatedAt == null));
    }

    /// <summary>
    /// A license without the chat module is the state of the listing, never an empty inbox (AC-NF106-1): «not available» with its reason, with no
    /// prerequisite to complete. A pass that failed for another reason, or none at all, is not a working sync; the initial import complete, or the
    /// recent pass, is.
    /// </summary>
    [Theory]
    [InlineData(null, true, false, true)]
    [InlineData(null, false, true, true)]
    [InlineData(null, false, false, false)]
    [InlineData("octorate_unavailable", true, true, false)]
    public async Task MessagesFlow_NeedsAPassThatWorked_AndAChatLicenseIsNotAvailableNeverEmpty(
        string? lastError,
        bool importCompleted,
        bool recentPass,
        bool syncWorks)
    {
        await ConnectAsync();
        var provider = new MessagesFlowProvider(_db, Reader(_world.Granted.Id, _world.Hidden.Id));
        _db.OtaMessageSyncStates.Add(SyncState(_world.Granted.Id, lastError, recentPass, importCompleted));
        _db.OtaMessageSyncStates.Add(SyncState(_world.Hidden.Id, OtaMessageSyncErrorCodes.ChatNotLicensed));
        await _db.SaveChangesAsync();

        var answer = await provider.EvaluateAsync(_world.OrgId, [_world.Granted.Id, _world.Hidden.Id], Ct);

        var granted = answer[_world.Granted.Id];
        Assert.Null(granted.NotAvailableReason);
        Assert.Equal(
            [OnboardingPrerequisiteKeys.Connection, OnboardingPrerequisiteKeys.Mapping, OnboardingPrerequisiteKeys.MessageSync],
            granted.Prerequisites.Select(p => p.Key));
        Assert.Equal(syncWorks, granted.Prerequisites.Single(p => p.Key == OnboardingPrerequisiteKeys.MessageSync).Met);
        Assert.All(granted.Prerequisites.Where(p => p.Key != OnboardingPrerequisiteKeys.MessageSync), p => Assert.True(p.Met));

        var noChat = answer[_world.Hidden.Id];
        Assert.True(noChat.Provided);
        Assert.Equal(OnboardingFlowReasons.ChatNotLicensed, noChat.NotAvailableReason);
        Assert.Empty(noChat.Prerequisites);
    }

    /// <summary>
    /// The state of a listing counts for the property it is confirmed to and the org it belongs to: a row with no property (its mapping is not confirmed)
    /// or of another org says nothing about this property, so the sync is not working for it.
    /// </summary>
    [Fact]
    public async Task MessagesFlow_ASyncStateOfNoPropertyOrOfAnotherOrg_IsNotTheStateOfTheProperty()
    {
        await ConnectAsync();
        var provider = new MessagesFlowProvider(_db, Reader(_world.Granted.Id));
        _db.OtaMessageSyncStates.Add(SyncState(null));
        _db.OtaMessageSyncStates.Add(SyncState(_world.Granted.Id, orgId: _world.OtherOrgId));
        await _db.SaveChangesAsync();

        var answer = await provider.EvaluateAsync(_world.OrgId, [_world.Granted.Id], Ct);

        var sync = answer[_world.Granted.Id].Prerequisites.Single(p => p.Key == OnboardingPrerequisiteKeys.MessageSync);
        Assert.False(sync.Met);
        Assert.Equal(OnboardingFlowReasons.MessageSyncNotReady, sync.Reason);
    }

    /// <summary>
    /// The documents flow follows the issuer the organization chose: no profile is «to complete» (<c>issuer_missing</c>, action <c>chooseIssuer</c>), the
    /// accountant is «not available», CasaZen needs no connection (and counts only while the issuance is on), Octorate needs the connection and the
    /// mapping too. The profile of another org counts for nobody here.
    /// </summary>
    [Fact]
    public async Task DocumentsFlow_FollowsTheIssuerTheOrganizationChose()
    {
        var provider = new DocumentsFlowProvider(_db, Reader(_world.Granted.Id), _flags.Object);
        var ids = new[] { _world.Granted.Id, _world.Hidden.Id };
        _db.OrgFiscalIssuerProfiles.Add(Issuer(DocumentIssuerMode.Accountant, _world.OtherOrgId));
        await _db.SaveChangesAsync();

        var none = await provider.EvaluateAsync(_world.OrgId, ids, Ct);
        (string Key, bool Met, string? Reason)[] issuerMissing = [(OnboardingPrerequisiteKeys.Issuer, false, OnboardingFlowReasons.IssuerMissing)];
        Assert.All(ids, id => Assert.Equal(issuerMissing, none[id].Prerequisites.Select(p => (p.Key, p.Met, p.Reason))));

        var profile = Issuer(DocumentIssuerMode.Accountant);
        _db.OrgFiscalIssuerProfiles.Add(profile);
        await _db.SaveChangesAsync();
        var accountant = await provider.EvaluateAsync(_world.OrgId, ids, Ct);
        Assert.All(ids, id =>
        {
            Assert.True(accountant[id].Provided);
            Assert.Equal(OnboardingFlowReasons.AccountantHandles, accountant[id].NotAvailableReason);
        });

        // CasaZen issues the direct bookings: the choice is enough, there is no connection to Octorate and none is needed (the issuance is on).
        _flags.Setup(f => f.IsEnabled(FeatureFlags.FiscalDocumentIssuance)).Returns(true);
        profile.IssuerMode = DocumentIssuerMode.CasaZenDirectOnly;
        await _db.SaveChangesAsync();
        var casazen = await provider.EvaluateAsync(_world.OrgId, ids, Ct);
        Assert.All(ids, id => Assert.Equal([OnboardingPrerequisiteKeys.Issuer], casazen[id].Prerequisites.Where(p => p.Met).Select(p => p.Key)));
        Assert.All(ids, id => Assert.Single(casazen[id].Prerequisites));

        // The issuance is switched off after the host chose CasaZen: nobody issues then (the issuer service answers «issuance off» for every booking), so
        // the choice is no choice and the flow leads the host to the wizard again. The accountant (above) and Octorate (below) do not depend on the flag.
        _flags.Setup(f => f.IsEnabled(FeatureFlags.FiscalDocumentIssuance)).Returns(false);
        var switchedOff = await provider.EvaluateAsync(_world.OrgId, ids, Ct);
        Assert.All(ids, id => Assert.Equal(issuerMissing, switchedOff[id].Prerequisites.Select(p => (p.Key, p.Met, p.Reason))));

        // Octorate issues: the connection and the confirmed listing of each property are needed too (the listing of Granted only is confirmed).
        profile.IssuerMode = DocumentIssuerMode.Octorate;
        await _db.SaveChangesAsync();
        var octorate = await provider.EvaluateAsync(_world.OrgId, ids, Ct);
        (string Key, bool Met, string? Reason)[] grantedNeeds =
        [
            (OnboardingPrerequisiteKeys.Issuer, true, null),
            (OnboardingPrerequisiteKeys.Connection, false, OnboardingFlowReasons.ConnectionMissing),
            (OnboardingPrerequisiteKeys.Mapping, true, null),
        ];
        Assert.Equal(grantedNeeds, octorate[_world.Granted.Id].Prerequisites.Select(p => (p.Key, p.Met, p.Reason)));
        Assert.Equal(
            (OnboardingFlowReasons.MappingUnconfirmed, false),
            octorate[_world.Hidden.Id].Prerequisites.Where(p => p.Key == OnboardingPrerequisiteKeys.Mapping).Select(p => (p.Reason!, p.Met)).Single());
    }

    /// <summary>
    /// A booking makes the reports ready only when it was imported (a reservation id of Octorate) and is still a booking: a request or a hold, a
    /// cancelled booking and a no-show are not (the one definition of an active booking). Without one the property is «to complete», never active.
    /// </summary>
    [Theory]
    [InlineData(BookingStatus.Confirmed, false, "RES-1", true)]
    [InlineData(BookingStatus.CheckedIn, false, "RES-1", true)]
    [InlineData(BookingStatus.CheckedOut, false, "RES-1", true)]
    [InlineData(BookingStatus.Pending, false, "RES-1", false)]
    [InlineData(BookingStatus.Cancelled, false, "RES-1", false)]
    [InlineData(BookingStatus.Confirmed, true, "RES-1", false)]
    [InlineData(BookingStatus.Confirmed, false, null, false)]
    [InlineData(BookingStatus.Confirmed, false, "  ", false)]
    public async Task ReportsFlow_NeedsAnImportedBookingThatIsActive(BookingStatus bookingStatus, bool noShow, string? reservationId, bool ready)
    {
        await ConnectAsync();
        var provider = new ReportsFlowProvider(_db, Reader(_world.Granted.Id, _world.Hidden.Id));
        await AddBookingAsync(_world.Granted, bookingStatus, reservationId, noShow);

        var answer = await provider.EvaluateAsync(_world.OrgId, [_world.Granted.Id, _world.Hidden.Id], Ct);

        var bookings = answer[_world.Granted.Id].Prerequisites.Single(p => p.Key == OnboardingPrerequisiteKeys.Bookings);
        Assert.Equal(ready, bookings.Met);
        Assert.Equal(ready ? null : OnboardingFlowReasons.NoBookingsYet, bookings.Reason);

        // The booking of one property is never the booking of another: Hidden has none imported.
        var hidden = answer[_world.Hidden.Id].Prerequisites.Single(p => p.Key == OnboardingPrerequisiteKeys.Bookings);
        Assert.False(hidden.Met);
    }

    /// <summary>
    /// «Imported» is a booking of a channel (the one definition, <see cref="FiscalCopy.IsOtaBookingSource"/>) with its reservation on Octorate: the
    /// bookings CasaZen made itself (the booking site, the host's own entry, the local ones) get an Octorate reservation id too when they are mirrored on
    /// the channel manager, and they were not imported from it, so they never make the reports ready. Every source of the enum is tried, so a new one is
    /// judged by the definition and not by a list written out here.
    /// </summary>
    [Fact]
    public async Task ReportsFlow_OnlyABookingOfAChannelCountsAsImported_NotOneCasaZenMirroredOnOctorate()
    {
        await ConnectAsync();
        var provider = new ReportsFlowProvider(_db, Reader(_world.Granted.Id));
        var sources = Enum.GetValues<BookingSource>();
        Assert.Contains(BookingSource.Manual, sources.Where(source => !FiscalCopy.IsOtaBookingSource(source)));
        Assert.Contains(BookingSource.OtherChannel, sources.Where(FiscalCopy.IsOtaBookingSource));

        foreach (var source in sources)
        {
            var booking = await AddBookingAsync(_world.Granted, BookingStatus.Confirmed, source: source);

            var answer = await provider.EvaluateAsync(_world.OrgId, [_world.Granted.Id], Ct);

            var bookings = answer[_world.Granted.Id].Prerequisites.Single(p => p.Key == OnboardingPrerequisiteKeys.Bookings);
            Assert.True(FiscalCopy.IsOtaBookingSource(source) == bookings.Met, $"{source}: imported = {FiscalCopy.IsOtaBookingSource(source)}, ready = {bookings.Met}");
            _db.Bookings.Remove(booking);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Proof (permission, AM-03): the providers answer only for the properties they are asked about and an empty list is an empty answer, never
    /// «every property of the org» (the org here has facts for both of its properties); and the flow status of a collaborator «Solo alcuni» holds only
    /// the property it was given, for the three flows.
    /// </summary>
    [Fact]
    public async Task Providers_AnswerOnlyForThePropertiesAsked_AndAnEmptyListIsAnEmptyAnswer()
    {
        await ConnectAsync();
        var reader = Reader(_world.Granted.Id, _world.Hidden.Id);
        _db.OtaMessageSyncStates.AddRange(SyncState(_world.Granted.Id), SyncState(_world.Hidden.Id));
        _db.OrgFiscalIssuerProfiles.Add(Issuer(DocumentIssuerMode.Octorate));
        await _db.SaveChangesAsync();
        await AddBookingAsync(_world.Granted, BookingStatus.Confirmed);
        await AddBookingAsync(_world.Hidden, BookingStatus.Confirmed);

        IFlowReadinessProvider[] providers = [new MessagesFlowProvider(_db, reader), new DocumentsFlowProvider(_db, reader, _flags.Object), new ReportsFlowProvider(_db, reader)];
        foreach (var provider in providers)
        {
            Assert.Empty(await provider.EvaluateAsync(_world.OrgId, [], Ct));
            Assert.Equal(
                [_world.Hidden.Id],
                (await provider.EvaluateAsync(_world.OrgId, [_world.Hidden.Id, _world.Hidden.Id], Ct)).Keys);
        }

        var status = FlowStatus(reader);
        var restricted = await status.GetStatusesAsync(_world.Restricted, null, CrossEpicFlows, Ct);
        var orgWide = await status.GetStatusesAsync(_world.OrgWide, null, CrossEpicFlows, Ct);
        Assert.Equal(3, restricted.Count);
        Assert.All(restricted, s => Assert.Equal(_world.Granted.Id, s.PropertyId));
        Assert.Equal(6, orgWide.Count);
        Assert.Equal(new[] { _world.Granted.Id, _world.Hidden.Id }.Order(), orgWide.Select(s => s.PropertyId).Distinct().Order());
    }

    // ---------------------------------------------------------------- the parts

    /// <summary>
    /// Proof (data, AC-NF104-9..11): the parts «Messages and AI», «Fiscal documents» and «Partners» show their progress from the stored facts of E05, E06
    /// and E09, and none is done without them. The preferences of a collaborator, and the row the alert router makes with the defaults, are not a save
    /// of an administrator; «none, my accountant issues» is done and says so in its reason; the assistant counts only while its flag is on.
    /// </summary>
    [Fact]
    public async Task OnboardingCrossEpicParts_StateComesFromStoredFacts_Test()
    {
        var messages = new MessagesAiPartEvaluator(_db, _flags.Object);
        var fiscal = new FiscalDocumentsPartEvaluator(_db, _flags.Object);
        var partners = new PartnersPartEvaluator(_flags.Object, Catalog());
        var context = new OnboardingPartContext(_world.OrgWide, [_world.Granted.Id, _world.Hidden.Id]);

        // Nothing stored: never done, each with the reason of what is missing.
        Assert.Equal(PartFacts.Counted(0, 1, OnboardingPartReasons.NotificationPreferencesMissing), await messages.EvaluateAsync(context, Ct));
        Assert.Equal(PartFacts.Counted(0, 1, OnboardingPartReasons.IssuerMissing), await fiscal.EvaluateAsync(context, Ct));
        Assert.Equal(PartFacts.NotAvailable(OnboardingPartReasons.ConnectorOff), await partners.EvaluateAsync(context, Ct));
        _flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        _flags.Setup(f => f.IsEnabled(FeatureFlags.MessageAssistant)).Returns(true);
        Assert.Equal(PartFacts.Counted(0, 2, OnboardingPartReasons.NotificationPreferencesMissing), await messages.EvaluateAsync(context, Ct));
        Assert.Equal(PartFacts.Counted(0, 1, OnboardingPartReasons.PartnerIntegrationMissing), await partners.EvaluateAsync(context, Ct));

        // What is not a save of an administrator: a collaborator's choice, and the defaults the alert router creates by itself.
        _db.UserNotificationChannelSettings.Add(ChannelSetting(_world.CollaboratorId));
        _db.UserNotificationPreferences.Add(new UserNotificationPreferences { OrgId = _world.OrgId, UserId = _world.OwnerUserId });
        await _db.SaveChangesAsync();
        Assert.Equal(0, (await messages.EvaluateAsync(context, Ct)).Done);

        // The owner saves its channels: the first check holds; the assistant has no situation yet.
        _db.UserNotificationChannelSettings.Add(ChannelSetting(_world.OwnerUserId));
        await _db.SaveChangesAsync();
        Assert.Equal(PartFacts.Counted(1, 2, OnboardingPartReasons.AssistantSituationMissing), await messages.EvaluateAsync(context, Ct));

        // A situation of the organization: both hold. With the assistant off the second check is not asked at all.
        _db.AssistantSituationSettings.Add(Situation(null));
        await _db.SaveChangesAsync();
        var both = await messages.EvaluateAsync(context, Ct);
        Assert.Equal((2, 2, true), (both.Done, both.Total, both.IsComplete));
        _flags.Setup(f => f.IsEnabled(FeatureFlags.MessageAssistant)).Returns(false);
        var onlyPreferences = await messages.EvaluateAsync(context, Ct);
        Assert.Equal((1, 1, true), (onlyPreferences.Done, onlyPreferences.Total, onlyPreferences.IsComplete));

        // The issuer: Octorate or CasaZen is a choice made, «none» is a choice too and says so in the reason.
        var profile = Issuer(DocumentIssuerMode.Octorate);
        _db.OrgFiscalIssuerProfiles.Add(profile);
        await _db.SaveChangesAsync();
        Assert.Equal(PartFacts.Counted(1, 1, null), await fiscal.EvaluateAsync(context, Ct));

        // «CasaZen issues» is a choice only while the issuance is on: switched off after the host chose it, nobody issues and the part is to do again.
        profile.IssuerMode = DocumentIssuerMode.CasaZenDirectOnly;
        await _db.SaveChangesAsync();
        Assert.Equal(PartFacts.Counted(0, 1, OnboardingPartReasons.IssuerMissing), await fiscal.EvaluateAsync(context, Ct));
        _flags.Setup(f => f.IsEnabled(FeatureFlags.FiscalDocumentIssuance)).Returns(true);
        Assert.Equal(PartFacts.Counted(1, 1, null), await fiscal.EvaluateAsync(context, Ct));

        profile.IssuerMode = DocumentIssuerMode.Accountant;
        await _db.SaveChangesAsync();
        var accountant = await fiscal.EvaluateAsync(context, Ct);
        Assert.Equal((true, 1, 1, OnboardingPartReasons.AccountantHandles), (accountant.Provided, accountant.Done, accountant.Total, accountant.Reason));
        Assert.True(accountant.IsComplete);

        // A partner declared by the host: done.
        _db.MarketplaceActivations.Add(Activation(null, MarketplaceActivationKind.Partner, MarketplaceActivationStatus.Active, declared: true));
        await _db.SaveChangesAsync();
        Assert.Equal(PartFacts.Counted(1, 1, null), await partners.EvaluateAsync(context, Ct));
    }

    /// <summary>
    /// Proof (permission, AM-03): the assistant exception of a property counts only for a caller who reaches that property, and an org with no property
    /// (or none reached) counts the rows of the organization only, never «every row».
    /// </summary>
    [Fact]
    public async Task MessagesAiPart_AnAssistantExceptionCountsOnlyForACallerWhoReachesTheProperty()
    {
        _flags.Setup(f => f.IsEnabled(FeatureFlags.MessageAssistant)).Returns(true);
        var evaluator = new MessagesAiPartEvaluator(_db, _flags.Object);
        var restricted = new OnboardingPartContext(_world.Restricted, [_world.Granted.Id]);
        var orgWide = new OnboardingPartContext(_world.OrgWide, [_world.Granted.Id, _world.Hidden.Id]);
        var noProperty = new OnboardingPartContext(_world.OrgWide, []);
        _db.AssistantSituationSettings.Add(Situation(_world.Hidden.Id));
        await _db.SaveChangesAsync();

        Assert.Equal(0, (await evaluator.EvaluateAsync(restricted, Ct)).Done);
        Assert.Equal(1, (await evaluator.EvaluateAsync(orgWide, Ct)).Done);
        Assert.Equal(0, (await evaluator.EvaluateAsync(noProperty, Ct)).Done);

        // The exception of the property the collaborator reaches counts for it; the row of the organization counts for everybody.
        _db.AssistantSituationSettings.Add(Situation(_world.Granted.Id, AssistantSituation.Parking));
        await _db.SaveChangesAsync();
        Assert.Equal(1, (await evaluator.EvaluateAsync(restricted, Ct)).Done);
        Assert.Equal(0, (await evaluator.EvaluateAsync(noProperty, Ct)).Done);

        _db.AssistantSituationSettings.Add(Situation(null, AssistantSituation.WiFi));
        await _db.SaveChangesAsync();
        foreach (var context in new[] { restricted, orgWide, noProperty })
            Assert.Equal(1, await DoneAsync(evaluator, context));
    }

    /// <summary>
    /// «Partners» reads the Active tab of the marketplace for the scope of the caller (E09): an integration of a property counts for a caller who reaches
    /// it, one of the organization only for a caller who reaches the whole org; a request that is not active nor declared, and a deactivated one, do not.
    /// </summary>
    [Fact]
    public async Task PartnersPart_ReadsTheActiveTabForTheScopeOfTheCaller()
    {
        _flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        var evaluator = new PartnersPartEvaluator(_flags.Object, Catalog());
        var restricted = new OnboardingPartContext(_world.Restricted, [_world.Granted.Id]);
        var orgWide = new OnboardingPartContext(_world.OrgWide, [_world.Granted.Id, _world.Hidden.Id]);
        _db.MarketplaceActivations.AddRange(
            Activation(_world.Granted.Id, MarketplaceActivationKind.Channel, MarketplaceActivationStatus.Requested, declared: false),
            Activation(_world.Hidden.Id, MarketplaceActivationKind.Channel, MarketplaceActivationStatus.Deactivated, declared: false));
        await _db.SaveChangesAsync();
        Assert.Equal(0, (await evaluator.EvaluateAsync(orgWide, Ct)).Done);

        // Active on the property the collaborator was NOT given: the owner sees it, the collaborator does not.
        _db.MarketplaceActivations.Add(Activation(_world.Hidden.Id, MarketplaceActivationKind.Tool, MarketplaceActivationStatus.Active, declared: false));
        await _db.SaveChangesAsync();
        Assert.Equal((0, 1), (await DoneAsync(evaluator, restricted), await DoneAsync(evaluator, orgWide)));

        // A partner declared for the whole org: the whole org sees it, the collaborator «Solo alcuni» does not.
        _db.MarketplaceActivations.Add(Activation(null, MarketplaceActivationKind.Partner, MarketplaceActivationStatus.Pending, declared: true));
        await _db.SaveChangesAsync();
        Assert.Equal((0, 1), (await DoneAsync(evaluator, restricted), await DoneAsync(evaluator, orgWide)));

        // Active on the property it was given: it counts for the collaborator too.
        _db.MarketplaceActivations.Add(Activation(_world.Granted.Id, MarketplaceActivationKind.Tool, MarketplaceActivationStatus.Active, declared: false));
        await _db.SaveChangesAsync();
        Assert.Equal((1, 1), (await DoneAsync(evaluator, restricted), await DoneAsync(evaluator, orgWide)));
    }

    /// <summary>
    /// The plan shows the three parts from their facts instead of «not provided» (AC-NF104-9..11), and «Partners» is optional: no part requires it, so
    /// it never blocks another part nor the activation.
    /// </summary>
    [Fact]
    public async Task Plan_ShowsThePartsOfTheOtherEpicsFromTheirFacts_AndPartnersIsOptional()
    {
        _flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        var activities = new InternalActivityService(_db, NullLogger<InternalActivityService>.Instance, new Casazen.Tests.Unit.FakeTimeProvider(HostScopeScenario.Now));
        IOnboardingPartEvaluator[] evaluators =
        [
            new MessagesAiPartEvaluator(_db, _flags.Object),
            new FiscalDocumentsPartEvaluator(_db, _flags.Object),
            new PartnersPartEvaluator(_flags.Object, Catalog()),
        ];
        var plans = new OnboardingPlanService(
            _db,
            evaluators,
            activities,
            new Casazen.Tests.Unit.FakeTimeProvider(HostScopeScenario.Now),
            NullLogger<OnboardingPlanService>.Instance);

        var before = await plans.GetPlanAsync(_world.OrgId, _world.OrgWide, Ct);
        (string State, string? Reason)[] open =
        [
            (OnboardingPartStates.Open, OnboardingPartReasons.NotificationPreferencesMissing),
            (OnboardingPartStates.Open, OnboardingPartReasons.IssuerMissing),
            (OnboardingPartStates.Open, OnboardingPartReasons.PartnerIntegrationMissing),
        ];
        Assert.Equal(
            open,
            new[] { OnboardingPartKey.MessagesAi, OnboardingPartKey.FiscalDocuments, OnboardingPartKey.Partners }
                .Select(key => before.Parts.Single(p => p.Key == key))
                .Select(part => (part.State, part.Reason)));

        _db.UserNotificationChannelSettings.Add(ChannelSetting(_world.OwnerUserId));
        _db.OrgFiscalIssuerProfiles.Add(Issuer(DocumentIssuerMode.Accountant));
        _db.MarketplaceActivations.Add(Activation(null, MarketplaceActivationKind.Partner, MarketplaceActivationStatus.Active, declared: true));
        await _db.SaveChangesAsync();
        var after = await plans.GetPlanAsync(_world.OrgId, _world.OrgWide, Ct);
        Assert.All(
            new[] { OnboardingPartKey.MessagesAi, OnboardingPartKey.FiscalDocuments, OnboardingPartKey.Partners },
            key => Assert.Equal((OnboardingPartStates.Done, (string?)null, 1, 1), After(after, key)));

        Assert.Empty(OnboardingPartCatalog.RequiredBy(OnboardingPartKey.Partners));
        Assert.True(OnboardingPartCatalog.IsDeferrable(OnboardingPartKey.Partners));
    }

    // ---------------------------------------------------------------- the codes and the registration

    /// <summary>The stable codes this task adds (add-only, never renamed) and the actions and default reasons that go with them.</summary>
    [Fact]
    public void TheStableCodes_AreAddOnly_AndEveryUnmetPrerequisiteHasItsReasonAndAction()
    {
        Assert.Equal("chat_not_licensed", OnboardingFlowReasons.ChatNotLicensed);
        Assert.Equal(OtaMessageSyncErrorCodes.ChatNotLicensed, OnboardingFlowReasons.ChatNotLicensed);
        Assert.Equal("issuer_missing", OnboardingFlowReasons.IssuerMissing);
        Assert.Equal("accountant_handles", OnboardingFlowReasons.AccountantHandles);
        Assert.Equal("no_bookings_yet", OnboardingFlowReasons.NoBookingsYet);
        Assert.Equal("message_sync_not_ready", OnboardingFlowReasons.MessageSyncNotReady);
        Assert.Equal("chooseIssuer", OnboardingFlowActions.ChooseIssuer);
        Assert.Equal(
            ["issuer", "messageSync", "bookings"],
            new[] { OnboardingPrerequisiteKeys.Issuer, OnboardingPrerequisiteKeys.MessageSync, OnboardingPrerequisiteKeys.Bookings });

        Assert.Equal(OnboardingFlowReasons.IssuerMissing, OnboardingPrerequisiteKeys.DefaultReasonOf(OnboardingPrerequisiteKeys.Issuer));
        Assert.Equal(OnboardingFlowReasons.NoBookingsYet, OnboardingPrerequisiteKeys.DefaultReasonOf(OnboardingPrerequisiteKeys.Bookings));
        Assert.Equal(OnboardingFlowReasons.MessageSyncNotReady, OnboardingPrerequisiteKeys.DefaultReasonOf(OnboardingPrerequisiteKeys.MessageSync));

        // Only the missing issuer is something the host fixes; the sync and the first booking come by themselves, and a «not available» reason has none.
        Assert.Equal(OnboardingFlowActions.ChooseIssuer, OnboardingFlowActions.ForReason(OnboardingFlowReasons.IssuerMissing));
        Assert.All(
            new[] { OnboardingFlowReasons.MessageSyncNotReady, OnboardingFlowReasons.NoBookingsYet, OnboardingFlowReasons.ChatNotLicensed, OnboardingFlowReasons.AccountantHandles },
            reason => Assert.Null(OnboardingFlowActions.ForReason(reason)));

        // The reasons of the parts that alias a reason of the flows are the same code.
        Assert.Equal(OnboardingFlowReasons.IssuerMissing, OnboardingPartReasons.IssuerMissing);
        Assert.Equal(OnboardingFlowReasons.AccountantHandles, OnboardingPartReasons.AccountantHandles);
        Assert.Equal(
            ["notification_preferences_missing", "assistant_situation_missing", "partner_integration_missing", "connector_off"],
            new[]
            {
                OnboardingPartReasons.NotificationPreferencesMissing,
                OnboardingPartReasons.AssistantSituationMissing,
                OnboardingPartReasons.PartnerIntegrationMissing,
                OnboardingPartReasons.ConnectorOff,
            });
    }

    /// <summary>
    /// The exact registrations of the activation (E10): one provider for each of the six flows and one evaluator for each of the eleven parts, in the
    /// order they are registered, none a placeholder (a factory) any more, all scoped.
    /// </summary>
    [Fact]
    public void Registration_EveryFlowAndEveryPartHasItsOwnProvider_NoPlaceholder()
    {
        var services = new ServiceCollection();
        services.AddCasazenOnboarding(new ConfigurationBuilder().Build());

        var providers = services.Where(d => d.ServiceType == typeof(IFlowReadinessProvider)).ToList();
        Assert.Equal(
            [
                typeof(ListingsFlowProvider),
                typeof(BookingsFlowProvider),
                typeof(CheckInComplianceFlowProvider),
                typeof(MessagesFlowProvider),
                typeof(DocumentsFlowProvider),
                typeof(ReportsFlowProvider),
            ],
            providers.Select(d => d.ImplementationType));
        var evaluators = services.Where(d => d.ServiceType == typeof(IOnboardingPartEvaluator)).ToList();
        Assert.Equal(
            [
                typeof(OctorateConnectionPartEvaluator),
                typeof(PropertiesPartEvaluator),
                typeof(ChannelsPartEvaluator),
                typeof(ListingsPartEvaluator),
                typeof(PricesRulesPartEvaluator),
                typeof(CompliancePartEvaluator),
                typeof(IdentificationPartEvaluator),
                typeof(ActivationPartEvaluator),
                typeof(MessagesAiPartEvaluator),
                typeof(FiscalDocumentsPartEvaluator),
                typeof(PartnersPartEvaluator),
            ],
            evaluators.Select(d => d.ImplementationType));
        Assert.All(providers.Concat(evaluators), d =>
        {
            Assert.Null(d.ImplementationFactory);
            Assert.Equal(ServiceLifetime.Scoped, d.Lifetime);
        });

        // What each one answers for: all six flows and all eleven parts, each at the order of a real provider (10), none at the placeholder's (0).
        var reader = Reader();
        IFlowReadinessProvider[] flows =
        [
            new MessagesFlowProvider(_db, reader),
            new DocumentsFlowProvider(_db, reader, _flags.Object),
            new ReportsFlowProvider(_db, reader),
        ];
        Assert.Equal(CrossEpicFlows, flows.Select(p => p.Flow));
        Assert.All(flows, p => Assert.Equal(FlowProviderOrders.Provider, p.Order));
        IOnboardingPartEvaluator[] parts =
        [
            new MessagesAiPartEvaluator(_db, _flags.Object),
            new FiscalDocumentsPartEvaluator(_db, _flags.Object),
            new PartnersPartEvaluator(_flags.Object, Catalog()),
        ];
        Assert.Equal([OnboardingPartKey.MessagesAi, OnboardingPartKey.FiscalDocuments, OnboardingPartKey.Partners], parts.Select(p => p.Part));
        Assert.All(parts, p => Assert.Equal(OnboardingPartEvaluatorOrders.Evaluator, p.Order));
    }

    // ---------------------------------------------------------------- the world

    private static (string State, string? Reason, string? Action) Of(IReadOnlyDictionary<OnboardingFlow, FlowPropertyStatus> statuses, OnboardingFlow flow)
    {
        var status = statuses[flow];
        return (status.State, status.Reason, status.Action);
    }

    private static (string State, string? Reason, int Done, int Total) After(OnboardingPlan plan, OnboardingPartKey key)
    {
        var part = plan.Parts.Single(p => p.Key == key);
        return (part.State, part.Reason, part.Done, part.Total);
    }

    private static async Task<int> DoneAsync(IOnboardingPartEvaluator evaluator, OnboardingPartContext context) =>
        (await evaluator.EvaluateAsync(context, Ct)).Done;

    private async Task ConnectAsync()
    {
        _db.OctorateConnections.Add(new OctorateConnection
        {
            OrgId = _world.OrgId,
            Environment = OctorateEnvironment.Test,
            Status = OctorateConnectionStatus.Connected,
        });
        await _db.SaveChangesAsync();
    }

    private FlowPrerequisiteReader Reader(params Guid[] confirmed)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Testing");
        return new FlowPrerequisiteReader(_db, new StubMappingReader(confirmed), environment.Object, NullLogger<FlowPrerequisiteReader>.Instance);
    }

    private OnboardingFlowStatusService FlowStatus(FlowPrerequisiteReader reader)
    {
        IFlowReadinessProvider[] providers = [new MessagesFlowProvider(_db, reader), new DocumentsFlowProvider(_db, reader, _flags.Object), new ReportsFlowProvider(_db, reader)];
        return new OnboardingFlowStatusService(_db, providers);
    }

    private async Task<IReadOnlyDictionary<OnboardingFlow, FlowPropertyStatus>> StatusesAsync(OnboardingFlowStatusService status) =>
        (await status.GetStatusesAsync(_world.OrgWide, [_world.Granted.Id], CrossEpicFlows, Ct)).ToDictionary(s => s.Flow);

    /// <summary>The real catalog of the marketplace over the same database: the Active tab the part «Partners» reads.</summary>
    private MarketplaceCatalogService Catalog()
    {
        var suppliers = new Mock<IHostSupplierUsageReader>();
        suppliers
            .Setup(s => s.ListAsync(It.IsAny<HostScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<HostUsedSupplier>());
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Testing");
        return new MarketplaceCatalogService(_db, suppliers.Object, _flags.Object, Mock.Of<IFileStorage>(), new ResxMarketplaceTexts(), environment.Object);
    }

    private PropertyFlowActivation Activation(Guid propertyId, OnboardingFlow flow) => new()
    {
        OrgId = _world.OrgId,
        PropertyId = propertyId,
        Flow = flow,
        ActivatedByUserId = _world.OwnerUserId,
    };

    private OtaMessageSyncState SyncState(
        Guid? propertyId,
        string? lastError = null,
        bool recentPass = true,
        bool importCompleted = false,
        Guid? orgId = null)
    {
        return new OtaMessageSyncState
        {
            OrgId = orgId ?? _world.OrgId,
            OctorateAccommodationId = $"acc-{Guid.NewGuid():N}",
            PropertyId = propertyId,
            ConnectionId = Guid.NewGuid(),
            LastError = lastError,
            LastRecentPassAt = recentPass ? HostScopeScenario.Now.UtcDateTime : null,
            InitialImportCompletedAt = importCompleted ? HostScopeScenario.Now.UtcDateTime : null,
            UpdatedAt = HostScopeScenario.Now.UtcDateTime,
        };
    }

    private OrgFiscalIssuerProfile Issuer(DocumentIssuerMode mode, Guid? orgId = null) => new()
    {
        OrgId = orgId ?? _world.OrgId,
        IssuerMode = mode,
    };

    private UserNotificationChannelSetting ChannelSetting(string userId) => new()
    {
        OrgId = _world.OrgId,
        UserId = userId,
        EventType = NotificationEventType.NewGuestMessage,
        Channels = NotificationChannels.Email,
    };

    private AssistantSituationSetting Situation(Guid? propertyId, AssistantSituation situation = AssistantSituation.CheckInTime) => new()
    {
        OrgId = _world.OrgId,
        PropertyId = propertyId,
        Situation = situation,
        Mode = AssistantMode.Draft,
    };

    private MarketplaceActivation Activation(Guid? propertyId, MarketplaceActivationKind kind, MarketplaceActivationStatus status, bool declared) => new()
    {
        OrgId = _world.OrgId,
        PropertyId = propertyId,
        Kind = kind,
        TargetCode = $"code-{Guid.NewGuid():N}",
        Status = status,
        IsDeclared = declared,
        DeclaredAt = declared ? HostScopeScenario.Now.UtcDateTime : null,
    };

    /// <summary>A booking of <paramref name="property"/> that came (or not, with no reservation id) from the channel manager.</summary>
    private async Task<Booking> AddBookingAsync(Property property, BookingStatus status, string? reservationId = "RES-1", bool noShow = false, BookingSource source = BookingSource.BookingCom)
    {
        var guestId = await _db.Guests.AsNoTracking().Where(g => g.OrgId == property.OrgId).Select(g => g.Id).FirstAsync();
        var booking = new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            GuestId = guestId,
            CheckInDate = HostScopeScenario.Day(2026, 12, 1),
            CheckOutDate = HostScopeScenario.Day(2026, 12, 4),
            NumberOfGuests = 2,
            Status = status,
            Source = source,
            // A blank or missing id stays as it is (that is the case under test); a real one is made unique for the org.
            OctorateReservationId = string.IsNullOrWhiteSpace(reservationId) ? reservationId : $"{reservationId}-{Guid.NewGuid():N}",
            NoShowAt = noShow ? HostScopeScenario.Now.UtcDateTime : null,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync();
        return booking;
    }

    /// <summary>The confirmed mapping of the properties named, and no other.</summary>
    private sealed class StubMappingReader(IReadOnlyCollection<Guid> confirmed) : IPropertyMappingReader
    {
        public bool IsProvided => true;

        public Task<IReadOnlyDictionary<Guid, string>> GetConfirmedAccommodationIdsAsync(
            Guid orgId,
            IReadOnlyCollection<Guid> propertyIds,
            CancellationToken ct)
        {
            IReadOnlyDictionary<Guid, string> answer = propertyIds.Where(confirmed.Contains).ToDictionary(id => id, id => $"acc-{id:N}");
            return Task.FromResult(answer);
        }
    }
}
