using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Octorate;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the check-in sent to Octorate (E03/F01/US01/T03, NF-41, ADR-023-R4, ADR-029): who sends decides <c>draft</c>, a property whose
/// sender is not the channel manager sends nothing while the switch is off, and a booking that is not mirrored causes no call and no row. The
/// Octorate client is a mock: what is proven is what CasaZen asks of it.
/// </summary>
public class CheckInOctorateSyncServiceTests
{
    private const string Accommodation = "585492";
    private const string Reservation = "7001";

    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a33");
    private static readonly DateTime CheckIn = new(2026, 11, 20, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OctorateCheckIn_DraftFalseOnlyWhenSenderIsChannelManager_Test()
    {
        // The pure rule, for every sender there is.
        Assert.All(
            Enum.GetValues<ComplianceSenderType>(),
            sender => Assert.Equal(sender != ComplianceSenderType.ChannelManager, OctorateCheckInMapper.DraftFor(sender)));

        // With the switch on every sender sends, and the payloads carry the rule: draft = false only for the channel manager.
        foreach (var sender in Enum.GetValues<ComplianceSenderType>())
        {
            var h = await Harness.CreateAsync(sender, sendWhenNotChannelManager: true);

            var result = await h.Service().SendGuestsAsync(h.BookingId, CancellationToken.None);

            var expectedDraft = sender != ComplianceSenderType.ChannelManager;
            Assert.Equal(OctorateCheckInSendState.Sent, result.State);
            Assert.Equal(expectedDraft, result.Draft);
            Assert.Equal(2, h.Sent.Count);
            Assert.All(h.Sent, upsert => Assert.Equal(expectedDraft, upsert.Draft));
            if (sender != ComplianceSenderType.ChannelManager)
                Assert.DoesNotContain(h.Sent, upsert => !upsert.Draft);

            // The Octorate id of each guest is kept, with the way it was sent.
            await using var db = h.NewDb();
            var links = await db.StayGuestOctorateLinks.OrderBy(l => l.Position).ToListAsync();
            Assert.Equal(new[] { 0, 1 }, links.Select(l => l.Position));
            Assert.All(links, link =>
            {
                Assert.Equal(OctorateSyncStatus.Sent, link.Status);
                Assert.Equal("guest-" + link.Position, link.OctorateGuestId);
                Assert.Equal(expectedDraft, link.SentAsDraft);
            });
        }
    }

    [Theory]
    [InlineData(ComplianceSenderType.CasaZen)]
    [InlineData(ComplianceSenderType.Manual)]
    public async Task OctorateCheckIn_SwitchOff_NoRequestForCasaZenOrManualSender_Test(ComplianceSenderType sender)
    {
        var h = await Harness.CreateAsync(sender, sendWhenNotChannelManager: false);

        var result = await h.Service().SendGuestsAsync(h.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.NotApplicable, result.State);
        Assert.Equal(CheckInOctorateSyncService.ReasonSenderNotChannelManager, result.Reason);
        Assert.Empty(result.Guests);
        h.Client.VerifyNoOtherCalls();
        await using var db = h.NewDb();
        Assert.Empty(await db.StayGuestOctorateLinks.ToListAsync());
    }

    [Fact]
    public async Task OctorateCheckIn_OnlyAStayThatOwesACommunicationSendsGuests_Test()
    {
        // A request still waiting (WAITING on Octorate), a cancelled booking and a no-show owe no police communication: nothing leaves, even with
        // the channel manager as sender, and no row is created.
        foreach (var (status, noShow) in new[] { (BookingStatus.Pending, false), (BookingStatus.Cancelled, false), (BookingStatus.Confirmed, true) })
        {
            var h = await Harness.CreateAsync(ComplianceSenderType.ChannelManager, status: status, noShow: noShow);

            var result = await h.Service().SendGuestsAsync(h.BookingId, CancellationToken.None);

            Assert.Equal(OctorateCheckInSendState.NotApplicable, result.State);
            Assert.Equal(CheckInOctorateSyncService.ReasonBookingNotActive, result.Reason);
            Assert.Empty(result.Guests);

            // The retry job goes through the same guard.
            var retried = await h.Service().RetryGuestsAsync(h.BookingId, CancellationToken.None);
            Assert.Equal(OctorateCheckInSendState.NotApplicable, retried.State);
            Assert.Equal(CheckInOctorateSyncService.ReasonBookingNotActive, retried.Reason);
            h.Client.VerifyNoOtherCalls();
            await using var db = h.NewDb();
            Assert.Empty(await db.StayGuestOctorateLinks.ToListAsync());
        }

        // The stays that do owe it, the set of the Alloggiati job: confirmed, checked in and checked out send their two guests.
        foreach (var status in new[] { BookingStatus.Confirmed, BookingStatus.CheckedIn, BookingStatus.CheckedOut })
        {
            var h = await Harness.CreateAsync(ComplianceSenderType.ChannelManager, status: status);

            var result = await h.Service().SendGuestsAsync(h.BookingId, CancellationToken.None);

            Assert.Equal(OctorateCheckInSendState.Sent, result.State);
            Assert.Equal(2, h.Sent.Count);
        }
    }

    [Fact]
    public async Task OctorateCheckIn_RetrySendsOnlyGuestsWhoseLinkIsPendingOrFailed_Test()
    {
        var h = await Harness.CreateAsync(ComplianceSenderType.ChannelManager);
        await h.AddGuestAsync(2);
        var oldHash = new string('a', StayGuestOctorateLink.PayloadHashLength);
        // Position 0 was sent and changed since (its payload is no longer the one sent); position 1 failed the last time; position 2 was
        // added after the confirmation and has no link at all.
        await h.AddLinkAsync(0, OctorateSyncStatus.Sent, attempts: 1, oldHash, "guest-0");
        await h.AddLinkAsync(1, OctorateSyncStatus.Failed, attempts: 2, oldHash, null);

        var retried = await h.Service().RetryGuestsAsync(h.BookingId, CancellationToken.None);

        // Only the failed position travels (created: it had no id); the changed guest and the added one are not pushed by the job.
        Assert.Equal(OctorateCheckInSendState.Sent, retried.State);
        Assert.False(retried.CorrectionAfterSend);
        Assert.Equal(1, Assert.Single(h.Sent).Position);
        await using (var db = h.NewDb())
        {
            var links = await db.StayGuestOctorateLinks.OrderBy(l => l.Position).ToListAsync();
            Assert.Equal(new[] { 0, 1 }, links.Select(l => l.Position));
            Assert.Equal(oldHash, links[0].PayloadHash);
            Assert.Equal(OctorateSyncStatus.Sent, links[1].Status);
            Assert.Equal("guest-1", links[1].OctorateGuestId);
        }

        // Nothing left to retry: the job sends nothing, whatever changed in the meantime.
        var again = await h.Service().RetryGuestsAsync(h.BookingId, CancellationToken.None);
        Assert.Equal(OctorateCheckInSendState.NothingToSend, again.State);
        Assert.Single(h.Sent);

        // The confirmation of the wizard (the full send) is what sends the guest that changed and the one that was added.
        var confirmed = await h.Service().SendGuestsAsync(h.BookingId, CancellationToken.None);
        Assert.Equal(OctorateCheckInSendState.Sent, confirmed.State);
        Assert.Equal(new[] { 0, 2 }, h.Sent.Skip(1).Select(u => u.Position).Order());
    }

    [Fact]
    public async Task OctorateCheckIn_NoShowIsSentOnlyWhileTheStayIsStillANoShow_Test()
    {
        // The first push failed and waits for the retry; the stay is still a no-show: the retry sends it.
        var still = await Harness.CreateAsync(ComplianceSenderType.ChannelManager, noShow: true);
        await still.AddStateRowAsync(OctorateStayState.NoShow, OctorateSyncStatus.Failed, attempts: 1);

        var sent = await still.Service().SetStayStateAsync(still.BookingId, OctorateStayState.NoShow, CancellationToken.None);

        Assert.Equal(OctorateStayStateOutcome.Sent, sent.Outcome);
        still.Client.Verify(
            c => c.SetCheckInStatusAsync(Accommodation, Reservation, OctorateCheckInStatus.NoShow, It.IsAny<CancellationToken>()), Times.Once);

        // The guests then arrived late: the host registered the arrival (the mark is cleared, the stay is checked in and CHECKIN_END went). The
        // retry of the NO_SHOW would reach Octorate last and mark a stay in progress as a no-show: nothing is called and the row is closed.
        var arrived = await Harness.CreateAsync(ComplianceSenderType.ChannelManager, status: BookingStatus.CheckedIn);
        await arrived.AddStateRowAsync(OctorateStayState.NoShow, OctorateSyncStatus.Failed, attempts: 1);

        var dropped = await arrived.Service().SetStayStateAsync(arrived.BookingId, OctorateStayState.NoShow, CancellationToken.None);

        Assert.Equal(OctorateStayStateOutcome.NotApplicable, dropped.Outcome);
        arrived.Client.VerifyNoOtherCalls();
        await using (var db = arrived.NewDb())
            Assert.Empty(await db.OctorateStayStateSyncs.ToListAsync());

        // The first push finds the arrival already registered: nothing is called and no row is made.
        var late = await Harness.CreateAsync(ComplianceSenderType.ChannelManager, status: BookingStatus.CheckedIn);

        var first = await late.Service().SetStayStateAsync(late.BookingId, OctorateStayState.NoShow, CancellationToken.None);

        Assert.Equal(OctorateStayStateOutcome.NotApplicable, first.Outcome);
        late.Client.VerifyNoOtherCalls();
        await using var lateDb = late.NewDb();
        Assert.Empty(await lateDb.OctorateStayStateSyncs.ToListAsync());
    }

    [Fact]
    public async Task OctorateCheckIn_ChangeAfterAGuestWasSentToThePolice_UpdatesOctorateAndWarnsTheHost_Test()
    {
        // The channel manager is the sender: the guests go with draft = false, that is Octorate transmits them to the police.
        var h = await Harness.CreateAsync(ComplianceSenderType.ChannelManager);
        var first = await h.Service().SendGuestsAsync(h.BookingId, CancellationToken.None);
        var unchanged = await h.Service().SendGuestsAsync(h.BookingId, CancellationToken.None);
        Assert.False(first.CorrectionAfterSend);
        Assert.Equal(OctorateCheckInSendState.NothingToSend, unchanged.State);
        Assert.False(unchanged.CorrectionAfterSend);
        Assert.Equal(2, h.Sent.Count);

        // A guest changes after the send: Octorate gets the update, with the id it gave, and the host is told that what was transmitted may need
        // a correction on the Alloggiati portal (AC-NF41-3). The other guest is not sent again.
        await h.ChangeGuestAsync(1, "Verdi");
        var corrected = await h.Service().SendGuestsAsync(h.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.Sent, corrected.State);
        Assert.True(corrected.CorrectionAfterSend);
        Assert.Contains(corrected.Guests, g => g.Position == 1 && g.Outcome == OctorateGuestSendOutcome.Updated);
        Assert.Contains(corrected.Guests, g => g.Position == 0 && g.Outcome == OctorateGuestSendOutcome.Unchanged);
        var update = h.Sent[^1];
        Assert.Equal(3, h.Sent.Count);
        Assert.Equal(1, update.Position);
        Assert.Equal("guest-1", update.OctorateGuestId);
        Assert.Equal("Verdi", update.FamilyName);

        // A guest sent as a draft (another sender, with the switch on) was not transmitted: changing it warns nobody.
        var draft = await Harness.CreateAsync(ComplianceSenderType.Manual, sendWhenNotChannelManager: true);
        await draft.Service().SendGuestsAsync(draft.BookingId, CancellationToken.None);
        await draft.ChangeGuestAsync(1, "Verdi");

        var changed = await draft.Service().SendGuestsAsync(draft.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.Sent, changed.State);
        Assert.False(changed.CorrectionAfterSend);
    }

    [Fact]
    public async Task OctorateCheckIn_AttemptIsOnRecordBeforeTheCall_AndASendCutShortIsRetried_Test()
    {
        var h = await Harness.CreateAsync(ComplianceSenderType.ChannelManager);
        var during = new List<(int Position, OctorateSyncStatus Status, int Attempts)>();
        h.Client
            .Setup(c => c.UpsertCheckInGuestsAsync(
                Accommodation, Reservation, It.IsAny<IReadOnlyList<OctorateCheckInGuestUpsert>>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                using var inside = h.NewDb();
                var links = inside.StayGuestOctorateLinks.OrderBy(l => l.Position).ToList();
                during.AddRange(links.Select(l => (l.Position, l.Status, l.Attempts)));
            })
            .Throws(new InvalidOperationException("the process was cut short"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service().SendGuestsAsync(h.BookingId, CancellationToken.None));

        // When the call was made both guests were already on record as pending, first attempt: nothing is lost if the call never comes back.
        Assert.Equal(new[] { (0, OctorateSyncStatus.Pending, 1), (1, OctorateSyncStatus.Pending, 1) }, during);

        // The retry job takes them up, and they go through.
        h.ClientSucceeds();
        var retried = await h.Service().RetryGuestsAsync(h.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.Sent, retried.State);
        await using var db = h.NewDb();
        Assert.All(await db.StayGuestOctorateLinks.ToListAsync(), link => Assert.Equal(OctorateSyncStatus.Sent, link.Status));
    }

    [Fact]
    public async Task OctorateCheckIn_AStateIsSentOnce_Test()
    {
        // NF-45, AC-NF45-1: a state of the stay goes to Octorate once; asked again, it answers that it was sent and calls nothing.
        var h = await Harness.CreateAsync(ComplianceSenderType.ChannelManager, noShow: true);

        var first = await h.Service().SetStayStateAsync(h.BookingId, OctorateStayState.NoShow, CancellationToken.None);
        var second = await h.Service().SetStayStateAsync(h.BookingId, OctorateStayState.NoShow, CancellationToken.None);

        Assert.Equal(OctorateStayStateOutcome.Sent, first.Outcome);
        Assert.Equal(OctorateStayStateOutcome.AlreadySent, second.Outcome);
        h.Client.Verify(
            c => c.SetCheckInStatusAsync(Accommodation, Reservation, OctorateCheckInStatus.NoShow, It.IsAny<CancellationToken>()), Times.Once);
        await using var db = h.NewDb();
        var row = await db.OctorateStayStateSyncs.SingleAsync();
        Assert.Equal(OctorateSyncStatus.Sent, row.Status);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task OctorateCheckIn_FailedGuestsAreRetried_OneTodoAtTheLastAttempt_ClosedWhenTheyGoThrough_Test()
    {
        var h = (await Harness.CreateAsync(ComplianceSenderType.ChannelManager)).MaxAttempts(3);
        h.ClientFails(CheckInErrorCodes.Server);

        // The confirmation of the wizard is the first attempt, the retry job the next ones: the code is on the links, and no "To do" yet.
        var first = await h.Service().SendGuestsAsync(h.BookingId, CancellationToken.None);
        var second = await h.Service().RetryGuestsAsync(h.BookingId, CancellationToken.None);
        Assert.Equal(OctorateCheckInSendState.Failed, first.State);
        Assert.Equal(OctorateCheckInSendState.Failed, second.State);
        Assert.All(first.Guests.Concat(second.Guests), g => Assert.Equal(CheckInErrorCodes.Server, g.ErrorCode));
        h.Activities.Verify(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // The third attempt is the last: one "To do" for the booking (no guest data in it), and its id is kept on the links.
        await h.Service().RetryGuestsAsync(h.BookingId, CancellationToken.None);
        h.Activities.Verify(
            a => a.CreateAsync(
                It.Is<CreateTodoRequest>(r => r.Reason == CheckInOctorateSyncService.TodoReason && r.EntityId == h.BookingId),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        await using (var db = h.NewDb())
        {
            Assert.All(
                await db.StayGuestOctorateLinks.ToListAsync(),
                link =>
                {
                    Assert.Equal(OctorateSyncStatus.Failed, link.Status);
                    Assert.Equal(3, link.Attempts);
                    Assert.Equal(Harness.TodoId, link.TodoActivityId);
                });
        }

        // The attempts are used up: the retry job leaves them alone (the host fixes the data and confirms again).
        var exhausted = await h.Service().RetryGuestsAsync(h.BookingId, CancellationToken.None);
        Assert.Equal(OctorateCheckInSendState.NothingToSend, exhausted.State);

        // The confirmation goes through at last: the guests are sent and the "To do" is closed.
        h.ClientSucceeds();
        var done = await h.Service().SendGuestsAsync(h.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.Sent, done.State);
        h.Activities.Verify(
            a => a.CloseAsync(Harness.TodoId, InternalActivity.ClosedBySystem, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        await using var after = h.NewDb();
        Assert.All(
            await after.StayGuestOctorateLinks.ToListAsync(),
            link =>
            {
                Assert.Equal(OctorateSyncStatus.Sent, link.Status);
                Assert.Null(link.TodoActivityId);
            });
    }

    [Fact]
    public async Task OctorateCheckIn_FailedStatePushIsRetried_OneTodoAtTheLastAttempt_ClosedWhenItGoesThrough_Test()
    {
        var h = (await Harness.CreateAsync(ComplianceSenderType.ChannelManager, noShow: true)).MaxAttempts(2);
        h.Client
            .Setup(c => c.SetCheckInStatusAsync(Accommodation, Reservation, OctorateCheckInStatus.NoShow, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));

        var first = await h.Service().SetStayStateAsync(h.BookingId, OctorateStayState.NoShow, CancellationToken.None);
        h.Activities.Verify(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        var second = await h.Service().SetStayStateAsync(h.BookingId, OctorateStayState.NoShow, CancellationToken.None);

        // Kept as failed with the code; the second attempt is the last, and raises the one "To do".
        Assert.Equal(OctorateStayStateOutcome.Failed, first.Outcome);
        Assert.Equal(CheckInErrorCodes.Server, first.ErrorCode);
        Assert.Equal(OctorateStayStateOutcome.Failed, second.Outcome);
        h.Activities.Verify(
            a => a.CreateAsync(It.Is<CreateTodoRequest>(r => r.Reason == CheckInOctorateSyncService.TodoReason), It.IsAny<CancellationToken>()),
            Times.Once);
        await using (var db = h.NewDb())
        {
            var failed = await db.OctorateStayStateSyncs.SingleAsync();
            Assert.Equal(OctorateSyncStatus.Failed, failed.Status);
            Assert.Equal(2, failed.Attempts);
            Assert.Equal(Harness.TodoId, failed.TodoActivityId);
        }

        // It goes through at last: sent, and the "To do" is closed.
        h.Client
            .Setup(c => c.SetCheckInStatusAsync(Accommodation, Reservation, OctorateCheckInStatus.NoShow, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var third = await h.Service().SetStayStateAsync(h.BookingId, OctorateStayState.NoShow, CancellationToken.None);

        Assert.Equal(OctorateStayStateOutcome.Sent, third.Outcome);
        h.Activities.Verify(a => a.CloseAsync(Harness.TodoId, InternalActivity.ClosedBySystem, It.IsAny<CancellationToken>()), Times.Once);
        await using var after = h.NewDb();
        var sent = await after.OctorateStayStateSyncs.SingleAsync();
        Assert.Equal(OctorateSyncStatus.Sent, sent.Status);
        Assert.Null(sent.TodoActivityId);
    }

    [Fact]
    public async Task OctorateCheckIn_NoWriteWithoutConfirmedMapping_Test()
    {
        // A listing only proposed, ambiguous or rejected: not mirrored.
        foreach (var status in new[] { OctorateMappingStatus.Proposed, OctorateMappingStatus.Ambiguous, OctorateMappingStatus.Rejected })
        {
            var h = await Harness.CreateAsync(ComplianceSenderType.ChannelManager, mappingStatus: status);
            await AssertNothingWrittenAsync(h, CheckInOctorateSyncService.ReasonMappingNotConfirmed);
        }

        // A confirmed listing whose writes are blocked: not mirrored either.
        var blocked = await Harness.CreateAsync(ComplianceSenderType.ChannelManager, writesBlocked: true);
        await AssertNothingWrittenAsync(blocked, CheckInOctorateSyncService.ReasonWritesBlocked);
    }

    /// <summary>No guest and no state of the stay leaves, no row is created, and the status says the booking is not mirrored.</summary>
    private static async Task AssertNothingWrittenAsync(Harness h, string reason)
    {
        var service = h.Service();

        var guests = await service.SendGuestsAsync(h.BookingId, CancellationToken.None);
        var state = await service.SetStayStateAsync(h.BookingId, OctorateStayState.CheckInEnd, CancellationToken.None);
        var view = await service.GetStatusAsync(h.BookingId, CancellationToken.None);

        Assert.Equal(OctorateCheckInSendState.NotMirrored, guests.State);
        Assert.Equal(reason, guests.Reason);
        Assert.Equal(OctorateStayStateOutcome.NotMirrored, state.Outcome);
        Assert.False(view.Mirrored);
        h.Client.VerifyNoOtherCalls();
        await using var db = h.NewDb();
        Assert.Empty(await db.StayGuestOctorateLinks.ToListAsync());
        Assert.Empty(await db.OctorateStayStateSyncs.ToListAsync());
    }

    /// <summary>
    /// A confirmed stay of org A on a property of a connected org (sandbox), with a reservation on Octorate and two registered guests (a head of
    /// family and a member); the sender of the property, the switch, the mapping and the write block are the test's.
    /// </summary>
    private sealed class Harness
    {
        /// <summary>The "To do" the activity service answers for the booking (the open one is returned each time, as the real service does).</summary>
        public static readonly Guid TodoId = Guid.Parse("7c1d9e20-3a4b-4c5d-8e6f-a1b2c3d4e5f6");

        private readonly string _database = $"octorate-checkin-{Guid.NewGuid():N}";
        private ComplianceSenderType _sender;
        private bool _sendWhenNotChannelManager;
        private bool _writesBlocked;
        private int _maxAttempts = OctorateOptions.DefaultCheckInMaxAttempts;

        private Harness()
        {
            ClientSucceeds();
            Activities
                .Setup(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(TodoId);
        }

        public Guid BookingId { get; private set; }

        public Guid PropertyId { get; private set; }

        public Mock<IOctorateApiClient> Client { get; } = new();

        public Mock<IInternalActivityService> Activities { get; } = new();

        /// <summary>Every guest payload the client received, in order of the calls.</summary>
        public List<OctorateCheckInGuestUpsert> Sent { get; } = [];

        /// <summary>The attempts of a write before the "To do" is raised (<c>Octorate:CheckInMaxAttempts</c>).</summary>
        public Harness MaxAttempts(int attempts)
        {
            _maxAttempts = attempts;
            return this;
        }

        /// <summary>The Octorate client accepts every guest: it creates the ones with no id and updates the others, and keeps what it was sent.</summary>
        public void ClientSucceeds() =>
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

        /// <summary>The Octorate client refuses every guest with <paramref name="errorCode"/> (a typed Octorate error as the client reports it).</summary>
        public void ClientFails(string errorCode) =>
            Client
                .Setup(c => c.UpsertCheckInGuestsAsync(
                    Accommodation, Reservation, It.IsAny<IReadOnlyList<OctorateCheckInGuestUpsert>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string _, IReadOnlyList<OctorateCheckInGuestUpsert> guests, CancellationToken _) =>
                {
                    Sent.AddRange(guests);
                    return guests.Select(g => new OctorateCheckInGuestResult(g.Position, null, false, null, errorCode)).ToArray();
                });

        /// <summary>A guest of the stay added after the first registration.</summary>
        public async Task AddGuestAsync(int position)
        {
            await using var db = NewDb();
            db.StayGuests.Add(StayGuestOf(BookingId, position, StayGuestType.FamilyMember, "Marta"));
            await db.SaveChangesAsync();
        }

        /// <summary>The surname of a registered guest changed after it was sent.</summary>
        public async Task ChangeGuestAsync(int position, string lastName)
        {
            await using var db = NewDb();
            var row = await db.StayGuests.SingleAsync(g => g.BookingId == BookingId && g.Position == position);
            row.LastName = lastName;
            await db.SaveChangesAsync();
        }

        /// <summary>The link of a guest as an earlier send left it.</summary>
        public async Task AddLinkAsync(int position, OctorateSyncStatus status, int attempts, string payloadHash, string? octorateGuestId)
        {
            await using var db = NewDb();
            db.StayGuestOctorateLinks.Add(new StayGuestOctorateLink
            {
                OrgId = OrgA,
                BookingId = BookingId,
                Position = position,
                OctorateGuestId = octorateGuestId,
                PayloadHash = payloadHash,
                Status = status,
                Attempts = attempts,
                SentAt = status == OctorateSyncStatus.Sent ? Now.UtcDateTime : null,
                UpdatedAt = Now.UtcDateTime.AddHours(-1),
            });
            await db.SaveChangesAsync();
        }

        /// <summary>The row of a state of the stay as an earlier push left it.</summary>
        public async Task AddStateRowAsync(OctorateStayState state, OctorateSyncStatus status, int attempts)
        {
            await using var db = NewDb();
            db.OctorateStayStateSyncs.Add(new OctorateStayStateSync
            {
                OrgId = OrgA,
                BookingId = BookingId,
                State = state,
                Status = status,
                Attempts = attempts,
                LastAttemptAt = Now.UtcDateTime.AddHours(-1),
                ErrorCode = status == OctorateSyncStatus.Failed ? CheckInErrorCodes.Server : null,
            });
            await db.SaveChangesAsync();
        }

        public static async Task<Harness> CreateAsync(
            ComplianceSenderType sender,
            bool sendWhenNotChannelManager = false,
            OctorateMappingStatus mappingStatus = OctorateMappingStatus.Confirmed,
            bool writesBlocked = false,
            BookingStatus status = BookingStatus.Confirmed,
            bool noShow = false)
        {
            var h = new Harness { _sender = sender, _sendWhenNotChannelManager = sendWhenNotChannelManager, _writesBlocked = writesBlocked };
            await h.SeedAsync(mappingStatus, status, noShow);
            return h;
        }

        public AppDbContext NewDb() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, NullTenantContext.Instance);

        public CheckInOctorateSyncService Service()
        {
            var db = NewDb();
            var stayGuests = new Mock<IStayGuestService>();
            stayGuests
                .Setup(s => s.GetForBookingAsync(It.IsAny<Booking>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Booking booking, CancellationToken _) =>
                    db.StayGuests.Where(g => g.BookingId == booking.Id).OrderBy(g => g.Position).ToList());
            var senders = new Mock<IPropertySenderStore>();
            senders
                .Setup(s => s.GetAsync(PropertyId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PropertySenderInfo(PropertyId, OrgA, _sender, _sender, SenderSource.Stored, true, null, null));
            var mapping = new Mock<IChannelMappingService>();
            mapping.Setup(m => m.IsWriteBlockedAsync(PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync(_writesBlocked);
            var flags = new Mock<IFeatureFlags>();
            flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
            var host = new Mock<IHostEnvironment>();
            host.SetupGet(e => e.EnvironmentName).Returns("Testing");

            return new CheckInOctorateSyncService(
                db,
                Client.Object,
                new OctorateOrgScope(NullTenantContext.Instance),
                stayGuests.Object,
                senders.Object,
                mapping.Object,
                Mock.Of<IStayTouristTaxService>(),
                Activities.Object,
                flags.Object,
                host.Object,
                Microsoft.Extensions.Options.Options.Create(new OctorateOptions
                {
                    SendGuestsWhenSenderIsNotChannelManager = _sendWhenNotChannelManager,
                    CheckInMaxAttempts = _maxAttempts,
                }),
                NullLogger<CheckInOctorateSyncService>.Instance,
                new FixedTimeProvider(Now));
        }

        private async Task SeedAsync(OctorateMappingStatus mappingStatus, BookingStatus status, bool noShow)
        {
            await using var db = NewDb();
            db.Orgs.Add(new OrgEntity { Id = OrgA, Name = "Org", Slug = $"org-{OrgA:N}", DisplayName = "Org" });
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
            var connection = new OctorateConnection
            {
                OrgId = OrgA,
                Environment = OctorateEnvironment.Test,
                Status = OctorateConnectionStatus.Connected,
            };
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
                OctorateReservationId = Reservation,
            };
            db.AddRange(property, connection, booker, booking);
            db.OctorateListingMappings.Add(new OctorateListingMapping
            {
                OrgId = OrgA,
                ConnectionId = connection.Id,
                OctorateAccommodationId = Accommodation,
                PropertyId = property.Id,
                Status = mappingStatus,
                SnapshotName = "Casa del Lago",
            });
            db.StayGuests.AddRange(
                StayGuestOf(booking.Id, 0, StayGuestType.HeadOfFamily, "Anna"),
                StayGuestOf(booking.Id, 1, StayGuestType.FamilyMember, "Luca"));
            await db.SaveChangesAsync();
            BookingId = booking.Id;
            PropertyId = property.Id;
        }

        private static StayGuest StayGuestOf(Guid bookingId, int position, StayGuestType type, string firstName) => new()
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
            CitizenshipCode = OctorateCheckInMapper.ItalyStateCode,
            CitizenshipName = "ITALIA",
            ResidenceInItaly = true,
            ResidenceComuneName = "Como",
            DocumentType = type == StayGuestType.HeadOfFamily ? GuestDocumentType.IdentityCard : null,
            DocumentNumber = type == StayGuestType.HeadOfFamily ? "AB1234567" : string.Empty,
            DocumentIssuePlaceName = type == StayGuestType.HeadOfFamily ? "Como" : string.Empty,
        };
    }
}
