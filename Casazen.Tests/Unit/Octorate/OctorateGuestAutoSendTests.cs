using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Octorate;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E20/F01/US01/T01 (decision O1): the guests of a stay are written to Octorate on their own, for an org that asked for it, when their data is complete
/// and the identity check passed; with the setting off (the default) nothing changes. The send is the one of the check-in wizard, with its rules: the
/// sender must be the channel manager for a send that is not a draft, one send for each change, the warning of a correction. A place Octorate does not
/// know holds that guest back; the arrival waits for the guests; the sweep finds the stays whose event was missed. On EF InMemory, the Octorate client a mock.
/// </summary>
public class OctorateGuestAutoSendTests
{
    private const string Accommodation = "585492";
    private const string Reservation = "7001";

    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a71");
    private static readonly Guid OrgB = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6b72");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTime CheckIn = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    // ─── The proofs ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (data, O1): with the setting off, the default, nothing is sent by itself, whatever the events say and whatever the stay is: the host's
    /// confirmation in the check-in wizard stays the only way. No call, no row, no To do.
    /// </summary>
    [Fact]
    public async Task AutoSend_SettingOff_NothingSent_Test()
    {
        var w = await World.CreateAsync(setting: false);

        foreach (var type in new[] { DomainEventType.StayGuestsChanged, DomainEventType.IdVerificationCompleted })
            await w.Handler().HandleAsync(w.Event(type), CancellationToken.None);
        var direct = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);

        Assert.Equal(StayGuestsAutoSendOutcome.SettingOff, direct.Outcome);
        w.Client.Verify(c => c.UpsertCheckInGuestsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<OctorateCheckInGuestUpsert>>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var db = w.NewDb();
        Assert.Empty(await db.StayGuestOctorateLinks.ToListAsync());
        w.Activities.Verify(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // The wizard's confirmation still sends (the setting never switches it off): the same stay, asked directly.
        var wizard = await w.Sync().SendGuestsAsync(w.BookingId, CancellationToken.None);
        Assert.Equal(OctorateCheckInSendState.Sent, wizard.State);
    }

    /// <summary>
    /// Proof (privacy, O1): while the identity check is on, no guest leaves before a passed verification: a stay with no verification, with a rejection
    /// or with the row that asks the guest for a new photo waits; the pass of the agent or of an operator sends it.
    /// </summary>
    [Fact]
    public async Task AutoSend_IdentityNotVerified_Waits_Test()
    {
        var w = await World.CreateAsync(identityCheck: true);

        var none = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);
        await w.AddIdentityLogAsync(BiometricOutcome.Reject, VerificationMethod.Operator, Now.UtcDateTime.AddMinutes(-30));
        var rejected = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);
        await w.AddIdentityLogAsync(BiometricOutcome.Escalate, VerificationMethod.Operator, Now.UtcDateTime.AddMinutes(-20));
        var photoAsked = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);

        Assert.All([none, rejected, photoAsked], r => Assert.Equal(StayGuestsAutoSendOutcome.IdentityNotVerified, r.Outcome));
        Assert.Empty(w.Sent);
        await using (var db = w.NewDb())
            Assert.Empty(await db.StayGuestOctorateLinks.ToListAsync());

        // The agent passes it: the guests go, with draft = false (the channel manager is the sender).
        await w.AddIdentityLogAsync(BiometricOutcome.Accept, VerificationMethod.Biometric, Now.UtcDateTime.AddMinutes(-5));
        var passed = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);
        Assert.Equal(StayGuestsAutoSendOutcome.Sent, passed.Outcome);
        Assert.Equal(OctorateCheckInSendState.Sent, passed.Send!.State);
        Assert.Equal(2, w.Sent.Count);

        // With the check off, the identity is not asked at all.
        var off = await World.CreateAsync(identityCheck: false);
        Assert.Equal(StayGuestsAutoSendOutcome.Sent, (await off.AutoSend().TrySendAsync(OrgA, off.BookingId, CancellationToken.None)).Outcome);
    }

    /// <summary>
    /// Proof (data, CHK-02 kept): a property whose sender is not the channel manager never sends anything that is not a draft, by itself either: with the
    /// switch of the drafts off nothing leaves; with it on every payload is a draft.
    /// </summary>
    [Theory]
    [InlineData(ComplianceSenderType.Manual)]
    [InlineData(ComplianceSenderType.CasaZen)]
    public async Task AutoSend_SenderNotChannelManager_NeverNonDraft_Test(ComplianceSenderType sender)
    {
        var off = await World.CreateAsync(sender: sender, sendDraftsWhenNotChannelManager: false);
        var held = await off.AutoSend().TrySendAsync(OrgA, off.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.NotApplicable, held.Send!.State);
        Assert.Equal(CheckInOctorateSyncService.ReasonSenderNotChannelManager, held.Send.Reason);
        Assert.Empty(off.Sent);

        var drafts = await World.CreateAsync(sender: sender, sendDraftsWhenNotChannelManager: true);
        await drafts.AutoSend().TrySendAsync(OrgA, drafts.BookingId, CancellationToken.None);

        Assert.NotEmpty(drafts.Sent);
        Assert.DoesNotContain(drafts.Sent, upsert => !upsert.Draft);
    }

    /// <summary>
    /// Proof (permission, ADR-022-R6): the handler works for the org of the event only. An event of org B that names a stay of org A touches nothing of it
    /// (no read of its guests, no call), and an org whose setting is off is never sent for because another org's is on.
    /// </summary>
    [Fact]
    public async Task StayGuestsChanged_OtherOrganizationIgnored_Test()
    {
        var w = await World.CreateAsync(setting: true, otherOrgSetting: true);

        // Org B asks for it too, but the stay is org A's: the event of B names it, and it is not found in B.
        await w.Handler().HandleAsync(w.Event(DomainEventType.StayGuestsChanged, orgId: OrgB), CancellationToken.None);
        var viaB = await w.AutoSend().TrySendAsync(OrgB, w.BookingId, CancellationToken.None);

        Assert.Equal(StayGuestsAutoSendOutcome.StayNotFound, viaB.Outcome);
        Assert.Empty(w.Sent);
        await using (var db = w.NewDb())
            Assert.Empty(await db.StayGuestOctorateLinks.ToListAsync());

        // Org A's own event does send.
        await w.Handler().HandleAsync(w.Event(DomainEventType.StayGuestsChanged), CancellationToken.None);
        Assert.Equal(2, w.Sent.Count);

        // The setting is read for the org of the event, never for another one.
        var off = await World.CreateAsync(setting: false, otherOrgSetting: true);
        Assert.Equal(StayGuestsAutoSendOutcome.SettingOff, (await off.AutoSend().TrySendAsync(OrgA, off.BookingId, CancellationToken.None)).Outcome);
    }

    // ─── What else the rule does ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AReadyStay_IsWrittenOnceThroughTheSendPathOfTheWizard_AndAnEventDeliveredAgainSendsNothing()
    {
        var w = await World.CreateAsync();

        var first = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);
        var again = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.Sent, first.Send!.State);
        Assert.False(first.Send.Draft);
        Assert.Equal(OctorateCheckInSendState.NothingToSend, again.Send!.State);
        Assert.Equal(2, w.Sent.Count);
        // The places went as Octorate's table names them, not as the guest typed them.
        Assert.All(w.Sent, upsert => Assert.Equal(("COMO", "COMO"), (upsert.BirthCity, upsert.ResidenceCity)));
        w.Activities.Verify(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AStayWhoseGuestsAreNotComplete_OrWhichOwesNothing_OrIsNotOnOctorate_SendsNothing()
    {
        var incomplete = await World.CreateAsync();
        await incomplete.EditGuestAsync(0, g => g.DocumentNumber = string.Empty);
        Assert.Equal(StayGuestsAutoSendOutcome.GuestsNotComplete, (await incomplete.AutoSend().TrySendAsync(OrgA, incomplete.BookingId, CancellationToken.None)).Outcome);

        var cancelled = await World.CreateAsync(status: BookingStatus.Cancelled);
        Assert.Equal(StayGuestsAutoSendOutcome.NotDue, (await cancelled.AutoSend().TrySendAsync(OrgA, cancelled.BookingId, CancellationToken.None)).Outcome);

        var noShow = await World.CreateAsync(noShow: true);
        Assert.Equal(StayGuestsAutoSendOutcome.NotDue, (await noShow.AutoSend().TrySendAsync(OrgA, noShow.BookingId, CancellationToken.None)).Outcome);

        var local = await World.CreateAsync(reservation: null);
        Assert.Equal(StayGuestsAutoSendOutcome.NotMirrored, (await local.AutoSend().TrySendAsync(OrgA, local.BookingId, CancellationToken.None)).Outcome);

        Assert.All([incomplete, cancelled, noShow, local], w => Assert.Empty(w.Sent));
    }

    /// <summary>
    /// A statistical field the region requires (ISTAT) never holds the guests back from the channel manager, which sends the police communication on: the
    /// automatic send applies the same rule as the confirmation of the wizard (<c>CheckInReadinessRules.SendBlockers</c>; the proof of the wizard is in
    /// <c>CheckInWizardServiceTests</c>). Here the region of the property requires the kind of tourism and no guest of the stay has given it: the guests are
    /// written all the same, and a field of the Alloggiati record that is missing still stops them.
    /// </summary>
    [Fact]
    public async Task AStayWhoseRegionRequiresAStatisticalField_IsWrittenAllTheSame_TheStatisticIsNotWhatTheChannelManagerNeeds()
    {
        var w = await World.CreateAsync();
        await using (var db = w.NewDb())
        {
            var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == w.BookingId);
            var property = await db.Properties.SingleAsync(p => p.Id == booking.PropertyId);
            property.ComuneIstatCode = "013075";
            db.Comuni.Add(new Comune
            {
                IstatCode = "013075",
                Name = "Como",
                DisplayName = "Como",
                NormalizedName = "como",
                SearchText = "como",
                ProvinceCode = "CO",
                RegionIstatCode = "03",
                RegionName = "Lombardia",
            });
            db.IstatRegionRules.Add(new IstatRegionRule
            {
                RegionIstatCode = "03",
                RegionName = "Lombardia",
                SourceUrl = "https://www.example.gov.it/flussi",
                RequiredGuestFields = IstatGuestFieldLists.TourismTypeField,
                VerifiedAt = Now.UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        var written = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);

        Assert.Equal(StayGuestsAutoSendOutcome.Sent, written.Outcome);
        Assert.Equal(2, w.Sent.Count);

        // A field the Alloggiati record needs is another matter: it still holds the guests back.
        var held = await World.CreateAsync();
        await held.EditGuestAsync(0, g => g.DocumentNumber = string.Empty);
        Assert.Equal(StayGuestsAutoSendOutcome.GuestsNotComplete, (await held.AutoSend().TrySendAsync(OrgA, held.BookingId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task TheHandler_ListensToTheTwoFactsThatMakeAStayReady_AndToNothingElse()
    {
        var w = await World.CreateAsync();

        foreach (var type in Enum.GetValues<DomainEventType>().Where(t => t is not (DomainEventType.StayGuestsChanged or DomainEventType.IdVerificationCompleted)))
            await w.Handler().HandleAsync(w.Event(type), CancellationToken.None);
        await w.Handler().HandleAsync(w.Event(DomainEventType.StayGuestsChanged, bookingId: Guid.Empty), CancellationToken.None);
        Assert.Empty(w.Sent);

        await w.Handler().HandleAsync(w.Event(DomainEventType.IdVerificationCompleted), CancellationToken.None);
        Assert.Equal(2, w.Sent.Count);
    }

    /// <summary>AC4: a host edit after the send goes once, through the hash, and warns about the correction (an urgent "To do"); a second event with nothing new sends and warns nothing.</summary>
    [Fact]
    public async Task AnEditAfterTheSend_IsSentOnce_AndRaisesTheCorrectionWarning()
    {
        var w = await World.CreateAsync();
        await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);
        w.Activities.Invocations.Clear();

        await w.EditGuestAsync(0, g => g.LastName = "Verdi");
        var corrected = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);
        var again = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);

        Assert.True(corrected.Send!.CorrectionAfterSend);
        Assert.Equal(OctorateGuestSendOutcome.Updated, Assert.Single(corrected.Send.Guests, g => g.Outcome != OctorateGuestSendOutcome.Unchanged).Outcome);
        Assert.False(again.Send!.CorrectionAfterSend);
        w.Activities.Verify(
            a => a.CreateAsync(
                It.Is<CreateTodoRequest>(r => r.Reason == StayGuestsAutoSendService.CorrectionTodoReason
                                              && r.Urgency == ActivityUrgency.High
                                              && r.EntityId == w.BookingId),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(3, w.Sent.Count);
    }

    /// <summary>Decision O2: a place the table does not know holds that guest back, and only that guest; one "To do" names the field; the retry never takes it; the corrected data sends it and closes the "To do".</summary>
    [Fact]
    public async Task APlaceOctorateDoesNotKnow_HoldsOnlyThatGuestBack_NamesTheField_AndIsNotRetried()
    {
        var w = await World.CreateAsync();
        await w.EditGuestAsync(1, g => g.ResidenceComuneName = "Atlantide");

        var held = await w.Sync().SendGuestsAsync(w.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.PartlyFailed, held.State);
        Assert.Equal(CheckInErrorCodes.PlaceNotRecognized, Assert.Single(held.Guests, g => g.Position == 1).ErrorCode);
        Assert.Equal([0], w.Sent.Select(s => s.Position));
        await using (var db = w.NewDb())
        {
            var link = await db.StayGuestOctorateLinks.SingleAsync(l => l.Position == 1);
            Assert.Equal((OctorateSyncStatus.Failed, CheckInErrorCodes.PlaceNotRecognized, "residenceComune"), (link.Status, link.ErrorCode, link.ErrorFields));
            Assert.Equal(Guid.Parse("7c1d9e20-3a4b-4c5d-8e6f-a1b2c3d4e5f6"), link.TodoActivityId);
        }

        w.Activities.Verify(
            a => a.CreateAsync(
                It.Is<CreateTodoRequest>(r => r.Reason == $"{CheckInOctorateSyncService.PlaceTodoReason}:residenceComune"), It.IsAny<CancellationToken>()),
            Times.Once);

        // A retry cannot correct a place: nothing is tried again, not even by the host asking to retry now.
        Assert.Equal(OctorateCheckInSendState.NothingToSend, (await w.Sync().RetryGuestsAsync(w.BookingId, CancellationToken.None)).State);
        Assert.Equal(OctorateCheckInSendState.NothingToSend, (await w.Sync().RetryGuestsNowAsync(w.BookingId, CancellationToken.None)).State);
        Assert.Single(w.Sent);

        // The host corrects the place: the guest goes, and the "To do" is closed.
        await w.EditGuestAsync(1, g => g.ResidenceComuneName = "Como");
        var fixedGuest = await w.Sync().SendGuestsAsync(w.BookingId, CancellationToken.None);
        Assert.Equal(OctorateCheckInSendState.Sent, fixedGuest.State);
        w.Activities.Verify(a => a.CloseAsync(It.IsAny<Guid>(), InternalActivity.ClosedBySystem, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ATableThatCannotBeRead_FailsTheGuestsThatNeedItWithTheCode_AndTheRetryTakesThemAgain()
    {
        var w = await World.CreateAsync();
        w.Client
            .Setup(c => c.GetCheckInCitiesAsync("IT", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));

        var failed = await w.Sync().SendGuestsAsync(w.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.Failed, failed.State);
        Assert.All(failed.Guests, g => Assert.Equal(CheckInErrorCodes.Server, g.ErrorCode));
        Assert.Empty(w.Sent);

        w.ClientKnowsTheCities();
        var retried = await w.Sync().RetryGuestsAsync(w.BookingId, CancellationToken.None);
        Assert.Equal(OctorateCheckInSendState.Sent, retried.State);
        Assert.Equal(2, w.Sent.Count);
    }

    /// <summary>The arrival goes after the guests of the stay: while a write of theirs is still to complete it waits, with no attempt used up; the departure never waits.</summary>
    [Fact]
    public async Task TheArrival_WaitsForTheGuestsWrite_ThenGoes_AndTheDepartureNeverWaits()
    {
        var w = await World.CreateAsync();
        await w.AddLinkAsync(0, OctorateSyncStatus.Pending, attempts: 1);
        w.Client.Setup(c => c.SetCheckInStatusAsync(Accommodation, Reservation, It.IsAny<OctorateCheckInStatus>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var waiting = await w.Sync().SetStayStateAsync(w.BookingId, OctorateStayState.CheckInEnd, CancellationToken.None);
        var departure = await w.Sync().SetStayStateAsync(w.BookingId, OctorateStayState.CheckOutEnd, CancellationToken.None);

        Assert.Equal(OctorateStayStateOutcome.WaitingForGuests, waiting.Outcome);
        Assert.Equal(OctorateStayStateOutcome.Sent, departure.Outcome);
        w.Client.Verify(c => c.SetCheckInStatusAsync(Accommodation, Reservation, OctorateCheckInStatus.CheckInEnd, It.IsAny<CancellationToken>()), Times.Never);
        await using (var db = w.NewDb())
        {
            var row = await db.OctorateStayStateSyncs.SingleAsync(s => s.State == OctorateStayState.CheckInEnd);
            Assert.Equal((OctorateSyncStatus.Pending, 0, null), (row.Status, row.Attempts, row.LastAttemptAt));
        }

        // The guests are written: the arrival goes (the retry job asks again).
        await w.SetLinksSentAsync();
        var sent = await w.Sync().SetStayStateAsync(w.BookingId, OctorateStayState.CheckInEnd, CancellationToken.None);
        Assert.Equal(OctorateStayStateOutcome.Sent, sent.Outcome);
        w.Client.Verify(c => c.SetCheckInStatusAsync(Accommodation, Reservation, OctorateCheckInStatus.CheckInEnd, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A stay with no guest to write, or whose guests failed for good and the host was told, is not held: the arrival is a fact.</summary>
    [Fact]
    public async Task TheArrival_IsNotHeldByAStayWithNothingToWrite_OrByAGuestHeldBackForAPlace()
    {
        var empty = await World.CreateAsync();
        empty.Client.Setup(c => c.SetCheckInStatusAsync(Accommodation, Reservation, It.IsAny<OctorateCheckInStatus>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Assert.Equal(OctorateStayStateOutcome.Sent, (await empty.Sync().SetStayStateAsync(empty.BookingId, OctorateStayState.CheckInEnd, CancellationToken.None)).Outcome);

        var held = await World.CreateAsync();
        held.Client.Setup(c => c.SetCheckInStatusAsync(Accommodation, Reservation, It.IsAny<OctorateCheckInStatus>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await held.AddLinkAsync(1, OctorateSyncStatus.Failed, attempts: 1, errorCode: CheckInErrorCodes.PlaceNotRecognized);
        await held.AddLinkAsync(0, OctorateSyncStatus.Failed, attempts: 5, errorCode: CheckInErrorCodes.Server);
        Assert.Equal(OctorateStayStateOutcome.Sent, (await held.Sync().SetStayStateAsync(held.BookingId, OctorateStayState.CheckInEnd, CancellationToken.None)).Outcome);

        // A write the retry job has not moved for longer than the limit is stuck (a rule keeps it from going on): the arrival is not held for ever.
        var stuck = await World.CreateAsync();
        stuck.Client.Setup(c => c.SetCheckInStatusAsync(Accommodation, Reservation, It.IsAny<OctorateCheckInStatus>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await stuck.AddLinkAsync(0, OctorateSyncStatus.Pending, attempts: 1, ageHours: (int)CheckInOctorateSyncService.ArrivalWaitLimit.TotalHours + 1);
        Assert.Equal(OctorateStayStateOutcome.Sent, (await stuck.Sync().SetStayStateAsync(stuck.BookingId, OctorateStayState.CheckInEnd, CancellationToken.None)).Outcome);
    }

    /// <summary>The state of the stay is tracked with a row, a retry and one "To do" (<c>octorate_stay_state_failed</c>) after the last attempt, closed by a later success.</summary>
    [Fact]
    public async Task AnArrivalOrDepartureThatKeepsFailing_IsTrackedAndRaisesTheStayStateTodoAtTheLastAttempt()
    {
        foreach (var (state, status) in new[]
                 {
                     (OctorateStayState.CheckInEnd, OctorateCheckInStatus.CheckInEnd),
                     (OctorateStayState.CheckOutEnd, OctorateCheckInStatus.CheckOutEnd),
                 })
        {
            var w = await World.CreateAsync(maxAttempts: 2);
            w.Client.Setup(c => c.SetCheckInStatusAsync(Accommodation, Reservation, status, It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateServerException(503));

            var first = await w.Sync().SetStayStateAsync(w.BookingId, state, CancellationToken.None);
            w.Activities.Verify(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Never);
            var second = await w.Sync().SetStayStateAsync(w.BookingId, state, CancellationToken.None);

            Assert.Equal(OctorateStayStateOutcome.Failed, first.Outcome);
            Assert.Equal(OctorateStayStateOutcome.Failed, second.Outcome);
            w.Activities.Verify(
                a => a.CreateAsync(It.Is<CreateTodoRequest>(r => r.Reason == CheckInOctorateSyncService.StayStateTodoReason), It.IsAny<CancellationToken>()),
                Times.Once);
            await using (var db = w.NewDb())
            {
                var row = await db.OctorateStayStateSyncs.SingleAsync();
                Assert.Equal((state, OctorateSyncStatus.Failed, 2, CheckInErrorCodes.Server), (row.State, row.Status, row.Attempts, row.ErrorCode));
            }

            w.Client.Setup(c => c.SetCheckInStatusAsync(Accommodation, Reservation, status, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Assert.Equal(OctorateStayStateOutcome.Sent, (await w.Sync().SetStayStateAsync(w.BookingId, state, CancellationToken.None)).Outcome);
            w.Activities.Verify(a => a.CloseAsync(It.IsAny<Guid>(), InternalActivity.ClosedBySystem, It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task TheSweep_FindsTheReadyStaysWhoseEventWasMissed_OnceForEachStayAndChange()
    {
        var w = await World.CreateAsync(setting: true);

        var missed = await w.AutoSend().FindStaysToSweepAsync(10, CancellationToken.None);
        Assert.Equal([(OrgA, w.BookingId)], missed);

        // The job sends it by the path of the handler; the next run does not take it again.
        var job = w.RetryJob();
        await job.ExecuteAsync(CancellationToken.None);
        Assert.Equal(2, w.Sent.Count);
        Assert.Empty(await w.AutoSend().FindStaysToSweepAsync(10, CancellationToken.None));
        await job.ExecuteAsync(CancellationToken.None);
        Assert.Equal(2, w.Sent.Count);

        // A guest changed after the last attempt: found again, once.
        await w.EditGuestAsync(1, g => g.LastName = "Neri", touch: Now.UtcDateTime.AddMinutes(10));
        Assert.Equal([(OrgA, w.BookingId)], await w.AutoSend().FindStaysToSweepAsync(10, CancellationToken.None));

        // An org that did not ask is never swept, and neither is a stay of another org for an org that did.
        var off = await World.CreateAsync(setting: false, otherOrgSetting: true);
        Assert.Empty(await off.AutoSend().FindStaysToSweepAsync(10, CancellationToken.None));
    }

    /// <summary>The guests saved put one event in the outbox, with the booking, the property and who saved them: ids and a word, never a guest's data.</summary>
    [Fact]
    public async Task SavingTheGuests_RecordsTheEventWithIdsOnly_ForTheOrgOfTheBooking()
    {
        var w = await World.CreateAsync();
        await using var db = w.NewDb();
        var booking = await db.Bookings.Include(b => b.Guest).SingleAsync();
        var service = new StayGuestService(
            db,
            new AlloggiatiCodeTableService(db, NullLogger<AlloggiatiCodeTableService>.Instance),
            new FixedTimeProvider(Now),
            new DomainEventRecorder(db, NullTenantContext.Instance, new FixedTimeProvider(Now)));

        var guest = new StayGuestInput
        {
            Type = "SingleGuest",
            FirstName = "Segreto",
            LastName = "Riservato",
            Gender = Gender.Male,
            DateOfBirth = new DateTime(1980, 1, 2),
            BornInItaly = true,
            BirthComuneName = "Como",
            BirthProvince = "CO",
            CitizenshipName = "ITALIA",
            DocumentType = "IdentityCard",
            DocumentNumber = "ZZ9999999",
            DocumentIssuePlaceName = "Como",
        };
        var saved = await service.ReplaceAsync(booking, [guest], StayGuestAuthor.Host("auth0|host"));
        await service.ReplaceAsync(booking, [guest], StayGuestAuthor.GuestPortal);

        Assert.True(saved.Success, string.Join(',', saved.Errors.Select(e => e.Field)));
        var events = await db.OutboxEvents.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal((DomainEventType.StayGuestsChanged, booking.Id, OrgA), (e.EventType, e.EntityId, e.OrgId));
            Assert.DoesNotContain("Segreto", e.PayloadJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Riservato", e.PayloadJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ZZ9999999", e.PayloadJson, StringComparison.Ordinal);
        });
        var authors = events.Select(e => JsonSerializer.Deserialize<StayGuestsChangedPayload>(e.PayloadJson)!).Select(p => p.Author).OrderBy(a => a).ToList();
        Assert.Equal([StayGuestsChangedAuthors.Guest, StayGuestsChangedAuthors.Host], authors);
        Assert.Equal(booking.PropertyId, JsonSerializer.Deserialize<StayGuestsChangedPayload>(events[0].PayloadJson)!.PropertyId);
    }

    [Fact]
    public async Task TheSettingOfTheOrg_IsOffByDefault_ChangesOnlyItsOwnOrg_AndKeepsWhoAndWhen()
    {
        var w = await World.CreateAsync(setting: false, otherOrgSetting: false);
        await using var db = w.NewDb();
        var setting = new OrgGuestAutoSendSetting(db, NullLogger<OrgGuestAutoSendSetting>.Instance, new FixedTimeProvider(Now));

        Assert.False(await setting.GetAsync(OrgA, CancellationToken.None));
        Assert.True(await setting.SetAsync(OrgA, true, "auth0|admin", CancellationToken.None));
        Assert.False(await setting.SetAsync(OrgA, true, "auth0|other", CancellationToken.None));

        Assert.True(await setting.GetAsync(OrgA, CancellationToken.None));
        Assert.False(await setting.GetAsync(OrgB, CancellationToken.None));
        Assert.False(await setting.GetAsync(Guid.NewGuid(), CancellationToken.None));
        var org = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == OrgA);
        Assert.Equal(("auth0|admin", Now.UtcDateTime), (org.AutoSendGuestsToOctorateChangedByUserId, org.AutoSendGuestsToOctorateChangedAt));
        await Assert.ThrowsAsync<Casazen.Core.Exceptions.NotFoundException>(() => setting.SetAsync(Guid.NewGuid(), true, "auth0|admin", CancellationToken.None));
    }

    // ─── After the review: the event is never lost, the places, the To do, the sweep ─────────────────────

    /// <summary>
    /// A change that arrives while another send of the stay holds the lock is not lost: the send answers «in progress», the event is not consumed
    /// (the dispatcher keeps it, with its attempt), and it is delivered again and then goes. The change made during that send is older than its link, so
    /// the sweep would never find it: only the redelivery does.
    /// </summary>
    [Fact]
    public async Task AnEventThatFindsTheLockOfTheStayTaken_IsDeliveredAgain_AndThenGoes()
    {
        var w = await World.CreateAsync();
        var handler = new StayGuestsReadyOctorateHandler(w.AutoSend(w.LockTakenOnce()), NullLogger<StayGuestsReadyOctorateHandler>.Instance);
        var eventId = await w.AddOutboxEventAsync();

        await w.RunDispatcherAsync(handler);

        var kept = await w.OutboxRowAsync(eventId);
        Assert.Equal((1, null), (kept.Attempts, kept.DispatchedAt));
        Assert.Contains(nameof(StayGuestsSendBusyException), kept.LastError, StringComparison.Ordinal);
        Assert.Empty(w.Sent);

        await w.RunDispatcherAsync(handler);

        var delivered = await w.OutboxRowAsync(eventId);
        Assert.NotNull(delivered.DispatchedAt);
        Assert.Equal(2, w.Sent.Count);
    }

    /// <summary>The service says it too: the lock taken is an exception, never an answer that looks like «sent», and nothing is written.</summary>
    [Fact]
    public async Task AutoSend_TheLockOfTheStayTaken_IsAnExceptionForTheOutboxAndTheSweep_NotAnAnswer()
    {
        var w = await World.CreateAsync();

        await Assert.ThrowsAsync<StayGuestsSendBusyException>(() => w.AutoSend(w.LockTakenOnce()).TrySendAsync(OrgA, w.BookingId, CancellationToken.None));

        Assert.Empty(w.Sent);
        await using var db = w.NewDb();
        Assert.Empty(await db.StayGuestOctorateLinks.ToListAsync());
    }

    /// <summary>
    /// A table of places that cannot be read (an error of Octorate, a rate limit or a full bucket) says nothing about a guest that is on Octorate for good:
    /// its link is not touched (no failure, no new hash, no new attempt), the answer says it was not looked at, and the change of a guest, which the sweep
    /// finds because the guest is newer than its link, goes with the warning of a correction when the table can be read.
    /// </summary>
    [Fact]
    public async Task ATableThatCannotBeRead_LeavesTheLinksOfTheGuestsOnOctorateAsTheyAre_AndTheChangeGoesWhenItCanBeRead()
    {
        var w = await World.CreateAsync();
        await w.Sync().SendGuestsAsync(w.BookingId, CancellationToken.None);
        var before = await w.LinksAsync();
        Assert.All(before, l => Assert.Equal(OctorateSyncStatus.Sent, l.Status));
        w.Client.Setup(c => c.GetCheckInCitiesAsync("IT", It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateRateLimitException(null));
        await w.EditGuestAsync(1, g => g.LastName = "Neri", touch: Now.UtcDateTime.AddMinutes(10));

        var blocked = await w.Sync().SendGuestsAsync(w.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.Failed, blocked.State);
        Assert.All(blocked.Guests, g => Assert.Equal(CheckInErrorCodes.RateLimited, g.ErrorCode));
        Assert.False(blocked.CorrectionAfterSend);
        Assert.Equal(2, w.Sent.Count);
        Assert.Equal(before, await w.LinksAsync());
        w.Activities.Verify(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // The guest is newer than its link: the sweep finds the stay, and the change goes, with the warning, once the table can be read.
        Assert.Equal([(OrgA, w.BookingId)], await w.AutoSend().FindStaysToSweepAsync(10, CancellationToken.None));
        w.ClientKnowsTheCities();
        var sent = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);

        Assert.True(sent.Send!.CorrectionAfterSend);
        Assert.Equal(3, w.Sent.Count);
        w.Activities.Verify(
            a => a.CreateAsync(It.Is<CreateTodoRequest>(r => r.Reason == StayGuestsAutoSendService.CorrectionTodoReason), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// AC4 after a hold: a guest sent for good, edited into a place Octorate does not know (held back, its link no longer says «sent») and then corrected,
    /// goes again with the warning of a correction: it is worked out from what was transmitted (a guest id, not a draft), never from the status.
    /// </summary>
    [Fact]
    public async Task AGuestSentForGood_HeldBackForAPlace_AndThenCorrected_StillRaisesTheCorrectionWarning()
    {
        var w = await World.CreateAsync();
        await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);
        Assert.Equal(2, w.Sent.Count);

        await w.EditGuestAsync(1, g => g.ResidenceComuneName = "Atlantide");
        var held = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);
        Assert.Equal(OctorateCheckInSendState.Failed, held.Send!.State);
        Assert.Equal(2, w.Sent.Count);
        await using (var db = w.NewDb())
            Assert.Equal(OctorateSyncStatus.Failed, (await db.StayGuestOctorateLinks.SingleAsync(l => l.Position == 1)).Status);

        await w.EditGuestAsync(1, g => g.ResidenceComuneName = "Roma");
        w.Activities.Invocations.Clear();
        var corrected = await w.AutoSend().TrySendAsync(OrgA, w.BookingId, CancellationToken.None);

        Assert.Equal(OctorateGuestSendOutcome.Updated, Assert.Single(corrected.Send!.Guests, g => g.Outcome != OctorateGuestSendOutcome.Unchanged).Outcome);
        Assert.True(corrected.Send.CorrectionAfterSend);
        Assert.Equal(3, w.Sent.Count);
        w.Activities.Verify(
            a => a.CreateAsync(It.Is<CreateTodoRequest>(r => r.Reason == StayGuestsAutoSendService.CorrectionTodoReason), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// One "To do" is open for each booking and several guests can hold it: it is closed only with the last of them, never while another guest is still held
    /// back for a place, and it names the place (urgent, so a milder cause that raised it first gives way to the reason of the place).
    /// </summary>
    [Fact]
    public async Task TheTodoOfTheBooking_NamesThePlace_AndIsClosedOnlyWhenNoOtherGuestStillNeedsIt()
    {
        var w = await World.CreateAsync();
        await w.EditGuestAsync(0, g => g.ResidenceComuneName = "Atlantide");
        await w.EditGuestAsync(1, g => g.ResidenceComuneName = "Narnia");
        const string placeReason = $"{CheckInOctorateSyncService.PlaceTodoReason}:residenceComune";

        var held = await w.Sync().SendGuestsAsync(w.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.Failed, held.State);
        w.Activities.Verify(
            a => a.CreateAsync(It.Is<CreateTodoRequest>(r => r.Reason == placeReason && r.Urgency == ActivityUrgency.High), It.IsAny<CancellationToken>()),
            Times.Once);
        w.Activities.Verify(
            a => a.RaiseUrgencyAsync(World.TodoId, ActivityUrgency.High, null, placeReason, It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // The first guest is corrected and goes: the second is still held back for a place, so the item stays open and names the place again.
        await w.EditGuestAsync(0, g => g.ResidenceComuneName = "Como");
        w.Activities.Invocations.Clear();
        var oneFixed = await w.Sync().SendGuestsAsync(w.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.PartlyFailed, oneFixed.State);
        Assert.Equal([0], w.Sent.Select(s => s.Position));
        w.Activities.Verify(a => a.CloseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        w.Activities.Verify(
            a => a.RaiseUrgencyAsync(World.TodoId, ActivityUrgency.High, null, placeReason, It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // The last guest is corrected and goes: nobody needs the item any more, so it is closed, once.
        await w.EditGuestAsync(1, g => g.ResidenceComuneName = "Como");
        var allFixed = await w.Sync().SendGuestsAsync(w.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.Sent, allFixed.State);
        w.Activities.Verify(a => a.CloseAsync(World.TodoId, InternalActivity.ClosedBySystem, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A guest that is written does not close the item an arrival that keeps failing still needs, and the arrival that goes does not close the item of a guest still failing.</summary>
    [Fact]
    public async Task TheTodoOfTheBooking_IsNotClosedByAWriteWhileAnotherWriteStillNeedsIt()
    {
        var w = await World.CreateAsync(maxAttempts: 2);
        w.Client
            .Setup(c => c.SetCheckInStatusAsync(Accommodation, Reservation, OctorateCheckInStatus.CheckInEnd, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));
        await w.Sync().SetStayStateAsync(w.BookingId, OctorateStayState.CheckInEnd, CancellationToken.None);
        await w.Sync().SetStayStateAsync(w.BookingId, OctorateStayState.CheckInEnd, CancellationToken.None);
        await w.AddLinkAsync(0, OctorateSyncStatus.Failed, attempts: 2, errorCode: CheckInErrorCodes.Server, todo: World.TodoId);
        w.Activities.Invocations.Clear();

        // The guests are written (the link that held the item goes): the arrival still needs it.
        var guests = await w.Sync().RetryGuestsNowAsync(w.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.Sent, guests.State);
        w.Activities.Verify(a => a.CloseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // The arrival goes at last: nothing else needs the item, so it is closed.
        w.Client.Setup(c => c.SetCheckInStatusAsync(Accommodation, Reservation, OctorateCheckInStatus.CheckInEnd, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Assert.Equal(OctorateStayStateOutcome.Sent, (await w.Sync().SetStayStateAsync(w.BookingId, OctorateStayState.CheckInEnd, CancellationToken.None)).Outcome);
        w.Activities.Verify(a => a.CloseAsync(World.TodoId, InternalActivity.ClosedBySystem, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The sweep looks at the ARRIVAL, from yesterday to 14 days ahead, as the runbook says: a stay that arrived earlier and is still in progress is not
    /// chased, however long it lasts, and one that arrives later than 14 days ahead has guests that are not ready.
    /// </summary>
    [Fact]
    public async Task TheSweep_LooksAtTheArrivalFromYesterdayTo14DaysAhead_NotAtAStayThatIsInProgress()
    {
        var w = await World.CreateAsync(setting: true);
        var yesterday = await w.AddStayAsync(-1);
        var edge = await w.AddStayAsync(StayGuestsAutoSendService.SweepLookaheadDays);
        await w.AddStayAsync(-2);
        await w.AddStayAsync(-30, nights: 60);
        await w.AddStayAsync(StayGuestsAutoSendService.SweepLookaheadDays + 1);

        var found = await w.AutoSend().FindStaysToSweepAsync(100, CancellationToken.None);

        Assert.Equal([yesterday, w.BookingId, edge], found.Select(f => f.BookingId));
    }

    /// <summary>
    /// The places of the sweep are spent on the stays that really went: more stays than a run writes, arriving earlier and not ready (their guests are
    /// incomplete), never take the place of the ready stay behind them.
    /// </summary>
    [Fact]
    public async Task TheSweep_AStayThatCannotGo_NeverTakesAPlaceFromAReadyOneBehindIt()
    {
        var w = await World.CreateAsync(setting: true);
        for (var i = 0; i < OctorateCheckInRetryJob.MaxSweepPerRun + 1; i++)
            await w.AddStayAsync(1 + (i % 3), complete: false);

        await w.RetryJob().ExecuteAsync(CancellationToken.None);

        // The ready stay (arriving after all of them) was written; the others stay as they were, and are looked at again by the next run.
        Assert.Equal(2, w.Sent.Count);
        await using var db = w.NewDb();
        Assert.Equal(2, await db.StayGuestOctorateLinks.CountAsync());
        Assert.Equal(OctorateCheckInRetryJob.MaxSweepPerRun + 1, (await w.AutoSend().FindStaysToSweepAsync(1_000, CancellationToken.None)).Count);
    }

    // ─── The world of a test ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Org A with a connected Octorate account and a confirmed listing, a property, a confirmed stay of two guests (a head of family and a member, complete)
    /// that is on Octorate, and org B (another org). The Octorate client accepts every guest and knows the cities of Italy.
    /// </summary>
    private sealed class World
    {
        public static readonly Guid TodoId = Guid.Parse("7c1d9e20-3a4b-4c5d-8e6f-a1b2c3d4e5f6");

        private readonly string _database = $"guest-autosend-{Guid.NewGuid():N}";
        private ComplianceSenderType _sender;
        private bool _identityCheck;
        private bool _drafts;
        private int _maxAttempts = OctorateOptions.DefaultCheckInMaxAttempts;

        private World()
        {
            ClientKnowsTheCities();
            Client
                .Setup(c => c.UpsertCheckInGuestsAsync(
                    Accommodation, Reservation, It.IsAny<IReadOnlyList<OctorateCheckInGuestUpsert>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string _, IReadOnlyList<OctorateCheckInGuestUpsert> guests, CancellationToken _) =>
                {
                    Sent.AddRange(guests);
                    return guests
                        .Select(g => new OctorateCheckInGuestResult(g.Position, g.OctorateGuestId ?? "guest-" + g.Position, g.OctorateGuestId is null, true, null))
                        .ToArray();
                });
            Activities.Setup(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(TodoId);

            // After an arrival the check-in sync reads the arrival Octorate registered (a failed read never fails the push).
            Client
                .Setup(c => c.GetReservationArrivalAsync(Accommodation, Reservation, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OctorateReservationArrival(null, null));
        }

        public Guid BookingId { get; private set; }

        public Mock<IOctorateApiClient> Client { get; } = new();

        public Mock<IInternalActivityService> Activities { get; } = new();

        /// <summary>Every guest payload the client received.</summary>
        public List<OctorateCheckInGuestUpsert> Sent { get; } = [];

        public static async Task<World> CreateAsync(
            bool setting = true,
            bool otherOrgSetting = false,
            ComplianceSenderType sender = ComplianceSenderType.ChannelManager,
            bool identityCheck = false,
            bool sendDraftsWhenNotChannelManager = false,
            BookingStatus status = BookingStatus.Confirmed,
            bool noShow = false,
            string? reservation = Reservation,
            int maxAttempts = OctorateOptions.DefaultCheckInMaxAttempts)
        {
            var w = new World { _sender = sender, _identityCheck = identityCheck, _drafts = sendDraftsWhenNotChannelManager, _maxAttempts = maxAttempts };
            await w.SeedAsync(setting, otherOrgSetting, status, noShow, reservation);
            return w;
        }

        public void ClientKnowsTheCities() =>
            Client
                .Setup(c => c.GetCheckInCitiesAsync("IT", It.IsAny<CancellationToken>()))
                .ReturnsAsync([new OctoratePlaceEntry("COMO", "403013075", "CITY"), new OctoratePlaceEntry("ROMA", "412058091", "CITY")]);

        public AppDbContext NewDb() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, NullTenantContext.Instance);

        public DomainEvent Event(DomainEventType type, Guid? orgId = null, Guid? bookingId = null) =>
            new(Guid.NewGuid(), orgId ?? OrgA, type, bookingId ?? BookingId, Now.UtcDateTime, "{}");

        public StayGuestsReadyOctorateHandler Handler() =>
            new(AutoSend(), NullLogger<StayGuestsReadyOctorateHandler>.Instance);

        public StayGuestsAutoSendService AutoSend(ICheckInOctorateSyncService? sync = null)
        {
            var db = NewDb();
            return new StayGuestsAutoSendService(
                db,
                new OrgGuestAutoSendSetting(db, NullLogger<OrgGuestAutoSendSetting>.Instance, new FixedTimeProvider(Now)),
                StayGuests(db).Object,
                sync ?? SyncOver(db),
                new OctorateOrgScope(NullTenantContext.Instance),
                Activities.Object,
                Flags().Object,
                NullLogger<StayGuestsAutoSendService>.Instance,
                new FixedTimeProvider(Now));
        }

        public CheckInOctorateSyncService Sync() => SyncOver(NewDb());

        public OctorateCheckInRetryJob RetryJob()
        {
            var db = NewDb();
            return new OctorateCheckInRetryJob(
                db,
                SyncOver(db),
                new OctorateOrgScope(NullTenantContext.Instance),
                Microsoft.Extensions.Options.Options.Create(new OctorateOptions { CheckInMaxAttempts = _maxAttempts }),
                NullLogger<OctorateCheckInRetryJob>.Instance,
                new FixedTimeProvider(Now),
                AutoSend());
        }

        /// <summary>
        /// Another stay of org A on Octorate (its own reservation), arriving <paramref name="arrivalInDays"/> days from today, with a head of family and a
        /// member, never written to Octorate. With <paramref name="complete"/> false the head has no document number: its guests are not ready.
        /// </summary>
        public async Task<Guid> AddStayAsync(int arrivalInDays, bool complete = true, int nights = 3)
        {
            await using var db = NewDb();
            var first = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == BookingId);
            var arrival = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc).AddDays(arrivalInDays);
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = OrgA,
                PropertyId = first.PropertyId,
                GuestId = first.GuestId,
                CheckInDate = arrival,
                CheckOutDate = arrival.AddDays(nights),
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Airbnb,
                NumberOfGuests = 2,
                BasePrice = 300m,
                TotalPrice = 300m,
                OctorateReservationId = $"stay-{Guid.NewGuid():N}",
            };
            var head = GuestOf(booking.Id, 0, StayGuestType.HeadOfFamily, "Mario");
            if (!complete)
                head.DocumentNumber = string.Empty;
            db.Bookings.Add(booking);
            db.StayGuests.AddRange(head, GuestOf(booking.Id, 1, StayGuestType.FamilyMember, "Rita"));
            await db.SaveChangesAsync();
            return booking.Id;
        }

        /// <summary>The links of the stay as they are stored, in the order of the guests.</summary>
        public async Task<List<(int Position, OctorateSyncStatus Status, string Hash, int Attempts, string? Code, string? Fields, DateTime UpdatedAt)>> LinksAsync()
        {
            await using var db = NewDb();
            var links = await db.StayGuestOctorateLinks.AsNoTracking().Where(l => l.BookingId == BookingId).OrderBy(l => l.Position).ToListAsync();
            return links.Select(l => (l.Position, l.Status, l.PayloadHash, l.Attempts, l.ErrorCode, l.ErrorFields, l.UpdatedAt)).ToList();
        }

        /// <summary>The event of the guests saved, in the outbox of the dispatcher, for this stay.</summary>
        public async Task<Guid> AddOutboxEventAsync()
        {
            await using var db = NewDb();
            var row = new OutboxEvent
            {
                OrgId = OrgA,
                EventType = DomainEventType.StayGuestsChanged,
                EntityId = BookingId,
                OccurredAt = Now.UtcDateTime,
                PayloadJson = "{}",
            };
            db.OutboxEvents.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        }

        /// <summary>One run of the outbox dispatcher over the events, with the bus that gives each event to <paramref name="handler"/>.</summary>
        public async Task RunDispatcherAsync(StayGuestsReadyOctorateHandler handler)
        {
            await using var db = NewDb();
            await new OutboxDispatcherJob(db, new HandlerBus(handler), new FixedTimeProvider(Now), NullLogger<OutboxDispatcherJob>.Instance)
                .ExecuteAsync(CancellationToken.None);
        }

        public async Task<OutboxEvent> OutboxRowAsync(Guid id)
        {
            await using var db = NewDb();
            return await db.OutboxEvents.AsNoTracking().SingleAsync(e => e.Id == id);
        }

        /// <summary>A send path whose first send finds the lock of the stay taken by another send, and every later one the real path.</summary>
        public ICheckInOctorateSyncService LockTakenOnce()
        {
            var real = Sync();
            var calls = 0;
            var sync = new Mock<ICheckInOctorateSyncService>();
            sync
                .Setup(s => s.SendGuestsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken ct) => calls++ == 0
                    ? Task.FromResult(new OctorateCheckInSendResult(
                        OctorateCheckInSendState.NothingToSend, CheckInOctorateSyncService.ReasonSendInProgress, false, [], false))
                    : real.SendGuestsAsync(id, ct));
            return sync.Object;
        }

        public async Task AddIdentityLogAsync(BiometricOutcome outcome, VerificationMethod method, DateTime at)
        {
            await using var db = NewDb();
            var booking = await db.Bookings.AsNoTracking().SingleAsync();
            db.BiometricVerificationLogs.Add(new BiometricVerificationLog
            {
                OrgId = OrgA,
                BookingId = BookingId,
                GuestId = booking.GuestId,
                Outcome = outcome,
                Method = method,
                Attempts = 1,
                ModelName = "test",
                ModelVersion = "1",
                ThresholdsJson = "{}",
                ReasonsJson = "[]",
                Provisional = false,
                CreatedAt = at,
            });
            await db.SaveChangesAsync();
        }

        public async Task EditGuestAsync(int position, Action<StayGuest> edit, DateTime? touch = null)
        {
            await using var db = NewDb();
            var row = await db.StayGuests.SingleAsync(g => g.BookingId == BookingId && g.Position == position);
            edit(row);
            row.UpdatedAt = touch ?? Now.UtcDateTime;
            await db.SaveChangesAsync();
        }

        public async Task AddLinkAsync(int position, OctorateSyncStatus status, int attempts, string? errorCode = null, int ageHours = 1, Guid? todo = null)
        {
            await using var db = NewDb();
            db.StayGuestOctorateLinks.Add(new StayGuestOctorateLink
            {
                OrgId = OrgA,
                BookingId = BookingId,
                Position = position,
                OctorateGuestId = status == OctorateSyncStatus.Sent ? "guest-" + position : null,
                PayloadHash = new string('a', StayGuestOctorateLink.PayloadHashLength),
                Status = status,
                Attempts = attempts,
                ErrorCode = errorCode,
                TodoActivityId = todo,
                UpdatedAt = Now.UtcDateTime.AddHours(-ageHours),
            });
            await db.SaveChangesAsync();
        }

        public async Task SetLinksSentAsync()
        {
            await using var db = NewDb();
            foreach (var link in await db.StayGuestOctorateLinks.ToListAsync())
            {
                link.Status = OctorateSyncStatus.Sent;
                link.OctorateGuestId ??= "guest-" + link.Position;
            }

            await db.SaveChangesAsync();
        }

        private CheckInOctorateSyncService SyncOver(AppDbContext db)
        {
            var senders = new Mock<IPropertySenderStore>();
            senders
                .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid propertyId, CancellationToken _) =>
                    new PropertySenderInfo(propertyId, OrgA, _sender, _sender, SenderSource.Stored, true, null, null));
            var mapping = new Mock<IChannelMappingService>();
            mapping.Setup(m => m.IsWriteBlockedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            var host = new Mock<IHostEnvironment>();
            host.SetupGet(e => e.EnvironmentName).Returns("Testing");

            return new CheckInOctorateSyncService(
                db,
                Client.Object,
                new OctorateOrgScope(NullTenantContext.Instance),
                StayGuests(db).Object,
                senders.Object,
                mapping.Object,
                Mock.Of<IStayTouristTaxService>(),
                Activities.Object,
                Flags().Object,
                host.Object,
                Microsoft.Extensions.Options.Options.Create(new OctorateOptions
                {
                    SendGuestsWhenSenderIsNotChannelManager = _drafts,
                    CheckInMaxAttempts = _maxAttempts,
                }),
                NullLogger<CheckInOctorateSyncService>.Instance,
                new FixedTimeProvider(Now),
                null,
                new OctoratePlaceNames(Client.Object, new MemoryCache(new MemoryCacheOptions())));
        }

        private Mock<IFeatureFlags> Flags()
        {
            var flags = new Mock<IFeatureFlags>();
            flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
            flags.Setup(f => f.IsEnabled(FeatureFlags.CheckInIdentityVerification)).Returns(_identityCheck);
            return flags;
        }

        private static Mock<IStayGuestService> StayGuests(AppDbContext db)
        {
            var stayGuests = new Mock<IStayGuestService>();
            stayGuests
                .Setup(s => s.GetForBookingAsync(It.IsAny<Booking>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Booking booking, CancellationToken _) =>
                    db.StayGuests.Where(g => g.BookingId == booking.Id).OrderBy(g => g.Position).ToList());
            return stayGuests;
        }

        private async Task SeedAsync(bool setting, bool otherOrgSetting, BookingStatus status, bool noShow, string? reservation)
        {
            await using var db = NewDb();
            db.Orgs.AddRange(
                new OrgEntity { Id = OrgA, Name = "Org A", Slug = $"org-{OrgA:N}", DisplayName = "Org A", AutoSendGuestsToOctorate = setting },
                new OrgEntity { Id = OrgB, Name = "Org B", Slug = $"org-{OrgB:N}", DisplayName = "Org B", AutoSendGuestsToOctorate = otherOrgSetting });
            var property = new Property
            {
                Id = Guid.NewGuid(),
                OrgId = OrgA,
                OwnerId = "auth0|host",
                Name = "Casa del Lago",
                Address = "Via del Lago 1",
                City = "Como",
                MaxGuests = 4,
            };
            var connection = new OctorateConnection { OrgId = OrgA, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
            var booker = new Guest { Id = Guid.NewGuid(), OrgId = OrgA, FirstName = "Anna", LastName = "Bianchi", Email = "anna@example.com" };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = OrgA,
                PropertyId = property.Id,
                GuestId = booker.Id,
                CheckInDate = CheckIn,
                CheckOutDate = CheckIn.AddDays(3),
                Status = status,
                NoShowAt = noShow ? Now.UtcDateTime : null,
                Source = BookingSource.Airbnb,
                NumberOfGuests = 2,
                BasePrice = 300m,
                TotalPrice = 300m,
                OctorateReservationId = reservation,
            };
            db.AddRange(property, connection, booker, booking);
            db.OctorateListingMappings.Add(new OctorateListingMapping
            {
                OrgId = OrgA,
                ConnectionId = connection.Id,
                OctorateAccommodationId = Accommodation,
                PropertyId = property.Id,
                Status = OctorateMappingStatus.Confirmed,
                SnapshotName = "Casa del Lago",
            });
            db.StayGuests.AddRange(GuestOf(booking.Id, 0, StayGuestType.HeadOfFamily, "Anna"), GuestOf(booking.Id, 1, StayGuestType.FamilyMember, "Luca"));
            await db.SaveChangesAsync();
            BookingId = booking.Id;
        }

        private static StayGuest GuestOf(Guid bookingId, int position, StayGuestType type, string firstName) => new()
        {
            BookingId = bookingId,
            OrgId = OrgA,
            Position = position,
            Type = type,
            FirstName = firstName,
            LastName = "Bianchi",
            Gender = Gender.Female,
            DateOfBirth = new DateTime(1985, 3, 2, 0, 0, 0, DateTimeKind.Utc),
            BornInItaly = true,
            BirthComuneName = "Como",
            BirthProvince = "CO",
            CitizenshipCode = OctorateCheckInMapper.ItalyStateCode,
            CitizenshipName = "ITALIA",
            ResidenceInItaly = true,
            ResidenceComuneName = "Como",
            DocumentType = type == StayGuestType.HeadOfFamily ? GuestDocumentType.IdentityCard : null,
            DocumentNumber = type == StayGuestType.HeadOfFamily ? "AB1234567" : string.Empty,
            DocumentIssuePlaceName = type == StayGuestType.HeadOfFamily ? "Como" : string.Empty,
            UpdatedAt = Now.UtcDateTime.AddDays(-1),
        };
    }

    /// <summary>The bus of a test: every event of the outbox goes to one handler, and a handler that throws fails the delivery, as the real bus does.</summary>
    private sealed class HandlerBus(StayGuestsReadyOctorateHandler handler) : IEventBus
    {
        public Task PublishAsync(DomainEvent domainEvent, CancellationToken ct) => handler.HandleAsync(domainEvent, ct);
    }
}
