using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using World = Casazen.Tests.Unit.Services.ComplianceStatusServiceTests.World;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The cockpit with its traffic light (E03/F02/US01/T03, NF-52): every item of the sections that existed carries a light and a stable reason, the
/// new sections hold the stays and properties of CHK-12, a stay that is not the host's to send by hand is not in "to send manually", the six
/// sections of before are the same items they were, and the cockpit is answered even when the To do list cannot be kept in step.
/// </summary>
public class ComplianceWizardStatusTests
{
    // 2026-11-06 11:00 in Rome.
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Summary_EveryItemCarriesALightAndAReason_AndTheNewSectionsHoldTheItemsOfChk12()
    {
        var world = await World.SeedAsync();
        world.Senders[world.Granted.Id] = ComplianceSenderType.ChannelManager;
        var (toSend, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2);
        var (failed, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2, report: AlloggiatiWebStatus.Errore);
        var (entrusted, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 2);
        var (longStay, _) = await world.AddStayAsync(world.Granted, checkInOffset: -1, nights: 40, guests: 1);
        var (declared, _) = await world.AddStayAsync(world.Hidden, checkInOffset: 0, report: AlloggiatiWebStatus.InviatoManualmente, reportedAt: Start.UtcDateTime.AddHours(-1));
        var (proven, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -1, report: AlloggiatiWebStatus.InviatoManualmente, reportedAt: Start.UtcDateTime.AddDays(-1));
        await world.EntrustAsync(entrusted, positions: [0, 1]);
        await world.AddProofAsync(world.Hidden.Id, ManualProofKind.AlloggiatiReceipt, DateOnly.FromDateTime(new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc)));
        // Octorate does not tell whether the send of the channel manager is active: the property asks the host to confirm. The rate of the comune is known.
        world.Rates.Add(new TouristTaxRate { City = "Ostuni", IsActive = true, EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), RatePerPersonPerNight = 2m });

        await using var db = world.Db();
        var summary = await Wizard(db, world).GetSummaryAsync(world.OrgWide);

        // The six sections of before: every item has a light and a code that says why.
        var older = new[]
            {
                summary.PropertiesPending, summary.GuestCheckInsIncomplete, summary.CheckoutsDue, summary.AlloggiatiFailures,
                summary.AlloggiatiManualRequired, summary.TurnoversPending,
            }
            .SelectMany(section => section.Items).ToList();
        Assert.NotEmpty(older);
        Assert.All(older, item =>
        {
            Assert.NotNull(item.Light);
            Assert.Contains(item.Reason, ComplianceReasonCodes.All);
        });

        // To send by hand: the stay nobody sent, in red; not the one entrusted to the channel manager, not the one over 30 nights (which has
        // its own section), not the one declared sent or the one with its receipt.
        var toSendItem = Assert.Single(summary.AlloggiatiManualRequired.Items, i => i.BookingId == toSend.Id);
        Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.DeadlineOverdue), (toSendItem.Light, toSendItem.Reason));
        Assert.Equal(ComplianceCockpitAction.SendAlloggiati, toSendItem.Action);
        foreach (var other in new[] { entrusted, longStay, declared, proven })
            Assert.DoesNotContain(summary.AlloggiatiManualRequired.Items, i => i.BookingId == other.Id);
        Assert.Equal(ComplianceCockpitAction.ResolveAlloggiatiFailure, Assert.Single(summary.AlloggiatiFailures.Items, i => i.BookingId == failed.Id).Action);
        Assert.Equal(ComplianceReasonCodes.AlloggiatiFailed, Assert.Single(summary.AlloggiatiFailures.Items, i => i.BookingId == failed.Id).Reason);

        // The receipts: the ones awaited (yellow) before the one archived (green); the count is what needs attention.
        var receipts = summary.ReceiptsToUpload;
        Assert.Equal(
            [ComplianceLight.Yellow, ComplianceLight.Yellow, ComplianceLight.Green],
            receipts.Items.Select(i => i.Light!.Value));
        Assert.Equal(2, receipts.Count);
        var entrustedItem = Assert.Single(receipts.Items, i => i.BookingId == entrusted.Id);
        Assert.Equal((ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceLight.Yellow, ComplianceReasonCodes.EntrustedToChannelManager),
            (entrustedItem.Action, entrustedItem.Light!.Value, entrustedItem.Reason));
        Assert.Equal((ComplianceEvidenceClass.ExternalVerificationRequired, OctorateHandoffTopic.Alloggiati), (entrustedItem.EvidenceClass!.Value, entrustedItem.HandoffTopic!.Value));
        Assert.NotEmpty(entrustedItem.Missing);
        Assert.NotNull(entrustedItem.DueAt);
        var provenItem = Assert.Single(receipts.Items, i => i.BookingId == proven.Id);
        Assert.Equal((ComplianceLight.Green, ComplianceReasonCodes.ReceiptArchived, ComplianceEvidenceClass.OfficialProof), (provenItem.Light!.Value, provenItem.Reason, provenItem.EvidenceClass!.Value));
        Assert.Equal(ComplianceEvidenceSources.ManualUpload, provenItem.EvidenceSource);
        Assert.NotNull(provenItem.LastVerifiedAt);
        Assert.Empty(provenItem.Missing);

        // The stay the channel manager does not send, and the connection nobody can verify, each in its own section.
        var manualSend = Assert.Single(summary.ManualSendsRequired.Items);
        Assert.Equal((longStay.Id, ComplianceCockpitAction.SendAlloggiatiManually, ComplianceReasonCodes.OverThirtyNightsNotSent), (manualSend.BookingId!.Value, manualSend.Action, manualSend.Reason));
        var connection = Assert.Single(summary.ConnectionsToFix.Items);
        Assert.Equal(
            (world.Granted.Id, ComplianceCockpitAction.ActivateOctorateSend, ComplianceLight.Yellow, ComplianceReasonCodes.ConnectionUnverified),
            (connection.PropertyId!.Value, connection.Action, connection.Light!.Value, connection.Reason));
        Assert.Equal(connection.PropertyId, connection.Id);
        Assert.Null(connection.BookingId);
        Assert.Empty(summary.IdentityPending.Items);
        Assert.Empty(summary.PeriodItems.Items);
    }

    [Fact]
    public async Task Summary_TheItemsOfBefore_AreTheSameWithTheStatusAsWithout_AndTheNewSectionsAreEmptyWithout()
    {
        var world = await World.SeedAsync();
        await world.AddStayAsync(world.Hidden, checkInOffset: -2);
        await world.AddStayAsync(world.Hidden, checkInOffset: -1, report: AlloggiatiWebStatus.Rifiutato);
        await world.AddStayAsync(world.Granted, checkInOffset: -3, nights: 5, status: BookingStatus.CheckedIn);

        await using var db = world.Db();
        var plain = await Wizard(db, world, withStatus: false).GetSummaryAsync(world.OrgWide);
        var lit = await Wizard(db, world).GetSummaryAsync(world.OrgWide);

        // Without the status service: the same six sections, as they were (no light), and nothing in the new ones.
        Assert.Empty(plain.IdentityPending.Items);
        Assert.Empty(plain.ReceiptsToUpload.Items);
        Assert.Empty(plain.ManualSendsRequired.Items);
        Assert.Empty(plain.ConnectionsToFix.Items);
        Assert.Empty(plain.PeriodItems.Items);
        Assert.All(plain.AlloggiatiManualRequired.Items, i => Assert.Null(i.Light));
        Assert.All(lit.AlloggiatiManualRequired.Items, i => Assert.Equal(ComplianceLight.Red, i.Light!.Value));

        // With it: the same items in the same sections (the id, the label, the action, the target, the deadline), the light aside.
        static IEnumerable<(Guid Id, string Label, ComplianceCockpitAction Action, Guid? PropertyId, Guid? BookingId, DateTime? DueAt)> Shape(ComplianceSummarySection s) =>
            s.Items.Select(i => (i.Id, i.Label, i.Action, i.PropertyId, i.BookingId, i.DueAt));
        Assert.Equal(Shape(plain.PropertiesPending), Shape(lit.PropertiesPending));
        Assert.Equal(Shape(plain.GuestCheckInsIncomplete), Shape(lit.GuestCheckInsIncomplete));
        Assert.Equal(Shape(plain.CheckoutsDue), Shape(lit.CheckoutsDue));
        Assert.Equal(Shape(plain.AlloggiatiFailures), Shape(lit.AlloggiatiFailures));
        Assert.Equal(Shape(plain.AlloggiatiManualRequired), Shape(lit.AlloggiatiManualRequired));
        Assert.Equal(Shape(plain.TurnoversPending), Shape(lit.TurnoversPending));
        Assert.Equal(plain.AlloggiatiManualRequired.Count, lit.AlloggiatiManualRequired.Count);
    }

    [Fact]
    public async Task Summary_TheToDoListIsFedOncePerSummary_AndAFailureOfTheFeedNeverFailsTheCockpit()
    {
        var world = await World.SeedAsync();
        await world.AddStayAsync(world.Hidden, checkInOffset: -2);
        var feeder = new Mock<IComplianceActivityFeeder>();
        feeder
            .Setup(f => f.FeedAsync(It.IsAny<HostScope>(), It.IsAny<ComplianceStatusResult>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the To do list is down"));

        await using var db = world.Db();
        var summary = await Wizard(db, world, feeder: feeder.Object).GetSummaryAsync(world.OrgWide);

        Assert.Single(summary.AlloggiatiManualRequired.Items);
        feeder.Verify(f => f.FeedAsync(world.OrgWide, It.Is<ComplianceStatusResult>(s => s.Items.Count > 0), It.IsAny<CancellationToken>()), Times.Once);

        // A cancelled request is not a failure of the feed: it is the caller's, and it goes on.
        feeder
            .Setup(f => f.FeedAsync(It.IsAny<HostScope>(), It.IsAny<ComplianceStatusResult>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => Wizard(db, world, feeder: feeder.Object).GetSummaryAsync(world.OrgWide));
    }

    [Fact]
    public async Task Summary_WithTheTrafficLightFlagOff_IsWhatItWasBefore_NothingEvaluatedAndNothingWritten()
    {
        var world = await World.SeedAsync();
        world.Senders[world.Granted.Id] = ComplianceSenderType.ChannelManager;
        var (toSend, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2);
        var (longStay, _) = await world.AddStayAsync(world.Granted, checkInOffset: -1, nights: 40, guests: 1);
        var (failed, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2, report: AlloggiatiWebStatus.Rifiutato);
        await world.AddSessionAsync((await world.AddStayAsync(world.Granted, checkInOffset: 1)).Stay);
        world.IdentityOn = true;

        var status = new Mock<IComplianceStatusService>(MockBehavior.Strict);
        var feeder = new Mock<IComplianceActivityFeeder>(MockBehavior.Strict);

        await using var db = world.Db();
        var off = await Wizard(db, world, feeder: feeder.Object, flagOn: false, status: status.Object).GetSummaryAsync(world.OrgWide);

        // The status is not evaluated and the feed is not run (a strict mock fails on any call), and nothing is in the To do list.
        status.VerifyNoOtherCalls();
        feeder.VerifyNoOtherCalls();
        Assert.Empty(await db.InternalActivities.AsNoTracking().ToListAsync());

        // The items carry no light, reason or evidence; the new sections are empty; the stay that would be left to its own section by the traffic
        // light (over 30 nights with the channel manager) is still in the one it was in.
        var everyItem = new[]
            {
                off.PropertiesPending, off.GuestCheckInsIncomplete, off.CheckoutsDue, off.AlloggiatiFailures, off.AlloggiatiManualRequired,
                off.TurnoversPending, off.IdentityPending, off.ReceiptsToUpload, off.ManualSendsRequired, off.ConnectionsToFix, off.PeriodItems,
            }
            .SelectMany(section => section.Items)
            .ToList();
        Assert.NotEmpty(everyItem);
        Assert.All(everyItem, item =>
        {
            Assert.Null(item.Light);
            Assert.Null(item.Reason);
            Assert.Null(item.Period);
            Assert.Null(item.EvidenceClass);
            Assert.Null(item.EvidenceSource);
            Assert.Null(item.LastVerifiedAt);
            Assert.Null(item.HandoffTopic);
        });
        foreach (var section in new[] { off.IdentityPending, off.ReceiptsToUpload, off.ManualSendsRequired, off.ConnectionsToFix, off.PeriodItems })
            Assert.Equal((0, 0), (section.Count, section.Items.Count));

        Assert.Equal(new[] { toSend.Id, longStay.Id }.Order(), off.AlloggiatiManualRequired.Items.Select(i => i.BookingId!.Value).Order());
        Assert.Equal(failed.Id, Assert.Single(off.AlloggiatiFailures.Items).BookingId);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    private static ComplianceWizardService Wizard(
        AppDbContext db,
        World world,
        bool withStatus = true,
        IComplianceActivityFeeder? feeder = null,
        bool flagOn = true,
        IComplianceStatusService? status = null)
    {
        var alloggiati = new Mock<IAlloggiatiWebService>();
        // The data of every guest is complete: this cockpit is about the traffic light, not about the check-in link.
        alloggiati.Setup(a => a.IsStayDataCompleteAsync(It.IsAny<Guid>())).ReturnsAsync(true);

        return new ComplianceWizardService(
            db,
            alloggiati.Object,
            Mock.Of<IStayLifecycleService>(),
            Mock.Of<ITouristTaxQuoteService>(),
            Mock.Of<IPropertyComplianceStatusService>(),
            NullLogger<ComplianceWizardService>.Instance,
            world.Clock,
            withStatus ? status ?? world.StatusService(db) : null,
            withStatus
                ? feeder ?? new ComplianceActivityFeeder(
                    new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock),
                    db,
                    new MemoryCache(new MemoryCacheOptions()),
                    NullLogger<ComplianceActivityFeeder>.Instance)
                : null,
            flagOn ? new TestFeatureFlags(FeatureFlags.ComplianceTrafficLight) : new TestFeatureFlags());
    }
}
