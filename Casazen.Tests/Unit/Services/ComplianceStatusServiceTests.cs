using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using AlloggiatiReceiptEntity = Casazen.Core.Entities.AlloggiatiReceipt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The compliance status read from the facts (E03/F02/US01/T03, NF-52, NF-50): which stays and properties it looks at (only the caller's reach),
/// what it takes for a proof (a live receipt, a receipt the host uploaded for that property and day, never a declaration or a handoff), when the
/// guests are entrusted to the channel manager, and which modules it needs.
/// </summary>
public class ComplianceStatusServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    private static DateTime Day(int offsetFromToday) => new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc).AddDays(offsetFromToday);

    [Fact]
    public async Task SwissStay_HasNoItalianPoliceObligation_InCockpitAndBookingStatus()
    {
        var world = await World.SeedAsync();
        var (stay, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2);
        await using var db = world.Db();
        (await db.Properties.SingleAsync(p => p.Id == world.Granted.Id)).Country = "CH";
        await db.SaveChangesAsync();

        var service = world.StatusService(db);
        var cockpit = await service.EvaluateAsync(world.OrgWide, CancellationToken.None);
        Assert.DoesNotContain(cockpit.Items, i => i.Kind == ComplianceItemKind.Alloggiati && i.BookingId == stay.Id);
        var status = await service.GetStayStatusAsync(world.OrgWide, stay.Id, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(AlloggiatiWebStatus.NotDue, status.Status);
        Assert.Null(status.Verdict);
        Assert.Empty(status.Actions);
    }

    [Fact]
    public async Task ComplianceStatus_RespectsHostScope_Test()
    {
        var world = await World.SeedAsync();
        var (grantedStay, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2);
        var (hiddenStay, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2);
        var (foreignStay, _) = await world.AddStayAsync(world.Foreign, checkInOffset: -2);

        await using var db = world.Db();
        var service = world.StatusService(db);

        var restricted = await service.EvaluateAsync(world.Restricted, CancellationToken.None);
        Assert.NotEmpty(restricted.Items);
        Assert.All(restricted.Items, item => Assert.Equal(world.Granted.Id, item.PropertyId));
        Assert.Equal([grantedStay.Id], restricted.Items.Where(i => i.BookingId is not null).Select(i => i.BookingId!.Value));
        Assert.Equal([world.Granted.Id], restricted.Evaluated.PropertyIds);
        Assert.Equal([grantedStay.Id], restricted.Evaluated.BookingIds);

        var orgWide = await service.EvaluateAsync(world.OrgWide, CancellationToken.None);
        Assert.Equal(
            new[] { grantedStay.Id, hiddenStay.Id }.Order(),
            orgWide.Items.Where(i => i.BookingId is not null).Select(i => i.BookingId!.Value).Order());
        Assert.DoesNotContain(orgWide.Items, i => i.PropertyId == world.Foreign.Id || i.BookingId == foreignStay.Id);
        Assert.Equal(new[] { world.Granted.Id, world.Hidden.Id }.Order(), orgWide.Evaluated.PropertyIds.Order());

        var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock);
        var feeder = new ComplianceActivityFeeder(activities, db, new MemoryCache(new MemoryCacheOptions()), NullLogger<ComplianceActivityFeeder>.Instance);
        await feeder.FeedAsync(world.Restricted, restricted, CancellationToken.None);

        var raised = await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        Assert.NotEmpty(raised);
        Assert.All(raised, a => Assert.Equal(world.Granted.Id, a.PropertyId));
        Assert.DoesNotContain(raised, a => a.EntityId == hiddenStay.Id || a.EntityId == world.Hidden.Id || a.EntityId == foreignStay.Id);
        Assert.Contains(raised, a => a.EntityType == InternalActivity.BookingEntityType && a.EntityId == grantedStay.Id);

        await feeder.FeedAsync(world.OrgWide, orgWide, CancellationToken.None);
        Assert.Contains(await db.InternalActivities.AsNoTracking().ToListAsync(), a => a.EntityId == hiddenStay.Id);
    }

    [Fact]
    public async Task ComplianceFeed_NoTodoStaysOpenForAStayThatOwesNothingAnyMore_Test()
    {
        var world = await World.SeedAsync();
        var (cancelled, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2);
        var (noShow, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2);
        var (stillOwes, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2);
        var (pastTheLookback, _) = await world.AddStayAsync(world.Granted, checkInOffset: -85);
        var (hiddenCancelled, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2);
        var (arrivingLater, _) = await world.AddStayAsync(world.Granted, checkInOffset: 20);

        await using var db = world.Db();
        ComplianceActivityFeeder Feeder() => new(
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock),
            db,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ComplianceActivityFeeder>.Instance);

        await Feeder().FeedAsync(world.OrgWide, await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None), CancellationToken.None);
        var others = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock);
        await others.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.AlloggiatiDeadline,
                Action = ActivityAction.UploadAlloggiatiProof,
                EntityType = InternalActivity.BookingEntityType,
                EntityId = arrivingLater.Id,
                Urgency = ActivityUrgency.High,
                PropertyId = world.Granted.Id,
            },
            CancellationToken.None);
        foreach (var stay in new[] { cancelled, noShow, stillOwes, pastTheLookback, hiddenCancelled })
            Assert.Single(await OpenTodosOfAsync(db, stay.Id));

        await world.UpdateStayAsync(cancelled, b => b.Status = BookingStatus.Cancelled);
        await world.UpdateStayAsync(noShow, b => b.NoShowAt = Day(-1));
        await world.UpdateStayAsync(hiddenCancelled, b => b.Status = BookingStatus.Cancelled);
        world.Clock.Advance(TimeSpan.FromDays(10));

        await Feeder().FeedAsync(world.Restricted, await world.StatusService(db).EvaluateAsync(world.Restricted, CancellationToken.None), CancellationToken.None);
        foreach (var gone in new[] { cancelled, noShow, pastTheLookback })
            Assert.Empty(await OpenTodosOfAsync(db, gone.Id));
        Assert.Single(await OpenTodosOfAsync(db, stillOwes.Id));
        Assert.Single(await OpenTodosOfAsync(db, hiddenCancelled.Id));
        Assert.Single(await OpenTodosOfAsync(db, arrivingLater.Id));

        var closed = await db.InternalActivities.AsNoTracking().SingleAsync(a => a.EntityId == cancelled.Id);
        Assert.Equal((InternalActivity.StatusCompleted, InternalActivity.ClosedBySystem), (closed.Status, closed.ClosedBy));

        await Feeder().FeedAsync(world.OrgWide, await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None), CancellationToken.None);
        Assert.Empty(await OpenTodosOfAsync(db, hiddenCancelled.Id));
        Assert.Single(await OpenTodosOfAsync(db, stillOwes.Id));
        Assert.Single(await OpenTodosOfAsync(db, arrivingLater.Id));
    }

    private static Task<List<InternalActivity>> OpenTodosOfAsync(AppDbContext db, Guid bookingId) =>
        db.InternalActivities.AsNoTracking().Where(a => a.Action != null && a.EntityId == bookingId && a.CompletedAt == null).ToListAsync();

    [Fact]
    public async Task ComplianceStatus_AnOctorateHandoffInTheAuditNeverChangesALight_Test()
    {
        var world = await World.SeedAsync();
        world.Senders[world.Granted.Id] = ComplianceSenderType.ChannelManager;
        var (entrusted, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 2);
        await world.EntrustAsync(entrusted, positions: [0, 1]);
        var (overdue, _) = await world.AddStayAsync(world.Granted, checkInOffset: -4, nights: 3, guests: 1);
        await world.EntrustAsync(overdue, positions: [0]);

        await using (var db = world.Db())
        {
            var before = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
            var yellow = ItemOf(before, entrusted).Verdict;
            Assert.Equal(
                (ComplianceLight.Yellow, ComplianceReasonCodes.EntrustedToChannelManager, OctorateHandoffTopic.Alloggiati),
                (yellow.Light, yellow.Reason, yellow.HandoffTopic!.Value));
            Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.ReceiptMissing), (ItemOf(before, overdue).Verdict.Light, ItemOf(before, overdue).Verdict.Reason));

            for (var line = 0; line < 2; line++)
            {
                db.OrgActivityEntries.Add(new OrgActivityEntry
                {
                    OrgId = world.Org.Id,
                    When = Start.UtcDateTime.AddMinutes(line),
                    ActorUserId = world.Owner.Id,
                    Area = OrgActivityArea.ShortRent,
                    Type = OrgActivityType.OctorateComplianceHandoffStarted,
                    SubjectType = OrgActivitySubjectType.Property,
                    SubjectId = world.Granted.Id.ToString(),
                    DetailsJson = "{}",
                });
            }

            await db.SaveChangesAsync();

            var after = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
            Assert.Equal(before.Items, after.Items);
        }
    }

    [Fact]
    public async Task Alloggiati_ARealReceiptOrOneTheHostUploadedForThatPropertyAndDay_IsTheProof()
    {
        var world = await World.SeedAsync();
        var (viaReceipt, reportA) = await world.AddStayAsync(world.Granted, checkInOffset: -3, report: AlloggiatiWebStatus.Inviato);
        var (viaSimulated, reportB) = await world.AddStayAsync(world.Granted, checkInOffset: -3, report: AlloggiatiWebStatus.Inviato);
        var (viaUpload, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2);
        var (otherDay, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -1);
        var (otherProperty, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2);
        await world.AddReceiptAsync(reportA!, AlloggiatiMode.Live);
        await world.AddReceiptAsync(reportB!, AlloggiatiMode.Simulated);
        await world.AddProofAsync(world.Hidden.Id, ManualProofKind.AlloggiatiReceipt, DateOnly.FromDateTime(Day(-2)));
        await world.AddProofAsync(world.Hidden.Id, ManualProofKind.AlloggiatiReceipt, DateOnly.FromDateTime(Day(-5)));
        await world.AddProofAsync(world.Hidden.Id, ManualProofKind.TouristTaxPayment, DateOnly.FromDateTime(Day(-1)));

        await using var db = world.Db();
        var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);

        var live = ItemOf(status, viaReceipt).Verdict;
        Assert.Equal((ComplianceLight.Green, ComplianceReasonCodes.ReceiptArchived, ComplianceEvidenceSources.CasazenReceipt), (live.Light, live.Reason, live.EvidenceSource));
        Assert.StartsWith("receipt:", live.ProofReference, StringComparison.Ordinal);
        Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.ReceiptMissing), (ItemOf(status, viaSimulated).Verdict.Light, ItemOf(status, viaSimulated).Verdict.Reason));
        var uploaded = ItemOf(status, viaUpload).Verdict;
        Assert.Equal((ComplianceLight.Green, ComplianceEvidenceSources.ManualUpload), (uploaded.Light, uploaded.EvidenceSource));
        Assert.StartsWith("manual-proof:", uploaded.ProofReference, StringComparison.Ordinal);
        Assert.Equal(ComplianceLight.Red, ItemOf(status, otherDay).Verdict.Light);
        Assert.Equal(ComplianceLight.Red, ItemOf(status, otherProperty).Verdict.Light);
    }

    [Fact]
    public async Task Alloggiati_DeclaredSentIsYellowTheDayOfTheSend_AndRedTheDayAfter_UntilTheReceiptIsUploaded()
    {
        var world = await World.SeedAsync();
        var (today, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, report: AlloggiatiWebStatus.InviatoManualmente, reportedAt: Start.UtcDateTime.AddHours(-1));
        var (yesterday, _) = await world.AddStayAsync(world.Granted, checkInOffset: -1, report: AlloggiatiWebStatus.InviatoManualmente, reportedAt: Start.UtcDateTime.AddDays(-1));
        var (pending, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, report: AlloggiatiWebStatus.SentReceiptPending, reportedAt: Start.UtcDateTime.AddHours(-2));

        await using var db = world.Db();
        var first = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);

        Assert.Equal((ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceLight.Yellow, ComplianceReasonCodes.AwaitingReceipt),
            (ItemOf(first, today).Action, ItemOf(first, today).Verdict.Light, ItemOf(first, today).Verdict.Reason));
        Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.ReceiptMissing), (ItemOf(first, yesterday).Verdict.Light, ItemOf(first, yesterday).Verdict.Reason));
        Assert.Equal(ComplianceLight.Yellow, ItemOf(first, pending).Verdict.Light);

        await world.AddProofAsync(world.Granted.Id, ManualProofKind.AlloggiatiReceipt, DateOnly.FromDateTime(Day(-1)));
        await world.AddProofAsync(world.Granted.Id, ManualProofKind.AlloggiatiReceipt, DateOnly.FromDateTime(Day(0)));
        await using var later = world.Db();
        var second = await world.StatusService(later).EvaluateAsync(world.OrgWide, CancellationToken.None);
        Assert.All(new[] { today, yesterday, pending }, stay => Assert.Equal(ComplianceLight.Green, ItemOf(second, stay).Verdict.Light));
    }

    [Fact]
    public async Task Alloggiati_EntrustedMeansEveryGuestIsOnOctorateForGood_AndOnlyWhileTheChannelManagerIsTheSender()
    {
        var world = await World.SeedAsync();
        world.Senders[world.Granted.Id] = ComplianceSenderType.ChannelManager;
        var (all, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 2);
        var (some, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 2);
        var (draft, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 2);
        var (failed, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 1);
        var (none, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 0);
        var (longStay, _) = await world.AddStayAsync(world.Granted, checkInOffset: -1, nights: 40, guests: 1);
        await world.EntrustAsync(all, positions: [0, 1]);
        await world.EntrustAsync(some, positions: [0]);
        await world.EntrustAsync(draft, positions: [0, 1], asDraft: true);
        await world.EntrustAsync(failed, positions: [0], status: OctorateSyncStatus.Failed);
        await world.EntrustAsync(longStay, positions: [0]);

        await using var db = world.Db();
        var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);

        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.EntrustedToChannelManager), (ItemOf(status, all).Verdict.Light, ItemOf(status, all).Verdict.Reason));
        foreach (var notEntrusted in new[] { some, draft, none })
        {
            Assert.Equal((ComplianceCockpitAction.SendAlloggiati, ComplianceLight.Red), (ItemOf(status, notEntrusted).Action, ItemOf(status, notEntrusted).Verdict.Light));
        }

        Assert.Equal(
            (ComplianceCockpitAction.ResolveAlloggiatiFailure, ComplianceLight.Red, ComplianceReasonCodes.AlloggiatiFailed),
            (ItemOf(status, failed).Action, ItemOf(status, failed).Verdict.Light, ItemOf(status, failed).Verdict.Reason));
        Assert.Equal(failed.Id, ItemOf(status, failed).TargetId);

        Assert.Equal((ComplianceCockpitAction.SendAlloggiatiManually, ComplianceReasonCodes.OverThirtyNightsNotSent),
            (ItemOf(status, longStay).Action, ItemOf(status, longStay).Verdict.Reason));

        world.ConnectorOn = false;
        await using var off = world.Db();
        var withoutConnector = await world.StatusService(off).EvaluateAsync(world.OrgWide, CancellationToken.None);
        Assert.Equal(ComplianceCockpitAction.SendAlloggiati, ItemOf(withoutConnector, all).Action);
        Assert.DoesNotContain(withoutConnector.Items, i => i.Kind == ComplianceItemKind.Connection);
    }

    [Fact]
    public async Task Alloggiati_ANoShowACancelledStayAndARequestOweNothing_AndAFailureIsResolvedNotSent()
    {
        var world = await World.SeedAsync();
        var (noShow, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2, noShow: true);
        var (cancelled, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2, status: BookingStatus.Cancelled);
        var (request, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2, status: BookingStatus.Pending);
        var (future, _) = await world.AddStayAsync(world.Granted, checkInOffset: 2);
        var (failed, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2, report: AlloggiatiWebStatus.Rifiutato);
        var (old, _) = await world.AddStayAsync(world.Granted, checkInOffset: -200);

        await using var db = world.Db();
        var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);

        foreach (var stay in new[] { noShow, cancelled, request, future, old })
            Assert.DoesNotContain(status.Items, i => i.BookingId == stay.Id);
        Assert.Equal((ComplianceCockpitAction.ResolveAlloggiatiFailure, ComplianceReasonCodes.AlloggiatiFailed), (ItemOf(status, failed).Action, ItemOf(status, failed).Verdict.Reason));
        Assert.Equal([failed.Id], status.Evaluated.BookingIds);
    }

    [Fact]
    public async Task Alloggiati_AStayWhosePropertyOrGuestIsGone_IsNotAnAlert_AndItsTodoCloses()
    {
        var world = await World.SeedAsync();
        var (open, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2);
        var (goneProperty, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2);
        var (goneGuest, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2);

        await using var db = world.Db();
        ComplianceActivityFeeder Feeder() => new(
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock),
            db,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ComplianceActivityFeeder>.Instance);

        await Feeder().FeedAsync(world.OrgWide, await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None), CancellationToken.None);
        Assert.Single(await OpenTodosOfAsync(db, open.Id));
        Assert.Single(await OpenTodosOfAsync(db, goneProperty.Id));
        Assert.Single(await OpenTodosOfAsync(db, goneGuest.Id));

        (await db.Properties.SingleAsync(p => p.Id == world.Hidden.Id)).IsDeleted = true;
        (await db.Guests.SingleAsync(g => g.Id == goneGuest.GuestId)).IsDeleted = true;
        await db.SaveChangesAsync();

        var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
        Assert.Contains(status.Items, i => i.BookingId == open.Id);
        Assert.DoesNotContain(status.Items, i => i.BookingId == goneProperty.Id || i.BookingId == goneGuest.Id);
        Assert.DoesNotContain(goneProperty.Id, status.Evaluated.BookingIds);
        Assert.DoesNotContain(goneGuest.Id, status.Evaluated.BookingIds);

        await Feeder().FeedAsync(world.OrgWide, status, CancellationToken.None);
        Assert.Single(await OpenTodosOfAsync(db, open.Id));
        Assert.Empty(await OpenTodosOfAsync(db, goneProperty.Id));
        Assert.Empty(await OpenTodosOfAsync(db, goneGuest.Id));
    }

    [Fact]
    public async Task Identity_OnlyWhileTheVerificationIsOn_AndOnlyForAStayWithACheckInSession()
    {
        var world = await World.SeedAsync();
        var (soon, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0);
        var (later, _) = await world.AddStayAsync(world.Granted, checkInOffset: 5);
        var (verified, _) = await world.AddStayAsync(world.Granted, checkInOffset: 1);
        var (escalated, _) = await world.AddStayAsync(world.Granted, checkInOffset: 6);
        var (decided, _) = await world.AddStayAsync(world.Granted, checkInOffset: 6);
        var (noSession, _) = await world.AddStayAsync(world.Granted, checkInOffset: 1);
        await world.AddSessionAsync(soon);
        await world.AddSessionAsync(later);
        await world.AddSessionAsync(verified);
        await world.AddSessionAsync(escalated, outcome: BiometricOutcome.Escalate);
        await world.AddSessionAsync(decided, outcome: BiometricOutcome.Escalate);
        await world.AddVerificationLogAsync(verified, BiometricOutcome.Accept, byOperator: false);
        await world.AddVerificationLogAsync(decided, BiometricOutcome.Escalate, byOperator: true);

        await using var off = world.Db();
        var without = await world.StatusService(off).EvaluateAsync(world.OrgWide, CancellationToken.None);
        Assert.DoesNotContain(without.Items, i => i.Kind == ComplianceItemKind.Identity);
        Assert.Empty(without.Evaluated.IdentityBookingIds);

        world.IdentityOn = true;
        await using var db = world.Db();
        var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);

        Assert.All(status.Items.Where(i => i.Kind == ComplianceItemKind.Identity), i => Assert.Equal(ComplianceCockpitAction.VerifyIdentity, i.Action));
        Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.IdentityMissing), (IdentityOf(status, soon).Light, IdentityOf(status, soon).Reason));
        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.SelfieAwaited), (IdentityOf(status, later).Light, IdentityOf(status, later).Reason));
        var passed = IdentityOf(status, verified);
        Assert.Equal((ComplianceLight.Green, ComplianceEvidenceSources.BiometricLog), (passed.Light, passed.EvidenceSource));
        Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.OperatorDecisionRequired), (IdentityOf(status, escalated).Light, IdentityOf(status, escalated).Reason));
        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.SelfieAwaited), (IdentityOf(status, decided).Light, IdentityOf(status, decided).Reason));
        Assert.Null(IdentityOf(status, decided).ProofReference);
        Assert.DoesNotContain(status.Items, i => i.Kind == ComplianceItemKind.Identity && i.BookingId == noSession.Id);
        Assert.Contains(noSession.Id, status.Evaluated.IdentityBookingIds);
    }

    [Fact]
    public async Task Identity_AnOperatorIsNeededExactlyWhenTheQueueWouldListTheSession()
    {
        var world = await World.SeedAsync();
        world.IdentityOn = true;

        var (asked, _) = await world.AddStayAsync(world.Granted, checkInOffset: 6);
        var (refused, _) = await world.AddStayAsync(world.Granted, checkInOffset: 6);
        var (attemptsUsed, _) = await world.AddStayAsync(world.Granted, checkInOffset: 6);
        var (consentGivenAttemptsUsed, _) = await world.AddStayAsync(world.Granted, checkInOffset: 6);
        var (reviewExpired, _) = await world.AddStayAsync(world.Granted, checkInOffset: 6);
        var (answered, _) = await world.AddStayAsync(world.Granted, checkInOffset: 6);
        var (waitingForTheSelfie, _) = await world.AddStayAsync(world.Granted, checkInOffset: 6);
        var (noShowIsNotAsked, _) = await world.AddStayAsync(world.Granted, checkInOffset: 6, noShow: true);
        await world.AddSessionAsync(asked, operatorRequested: true);
        await world.AddSessionAsync(refused, consentRefused: true);
        await world.AddSessionAsync(attemptsUsed, outcome: BiometricOutcome.Reject, attempts: VerificationOperatorRules.MaxAttempts);
        await world.AddSessionAsync(consentGivenAttemptsUsed, outcome: BiometricOutcome.Reject, attempts: VerificationOperatorRules.MaxAttempts, consentGiven: true);
        await world.AddSessionAsync(reviewExpired, outcome: BiometricOutcome.Escalate, expired: true);
        await world.AddSessionAsync(answered, operatorRequested: true);
        await world.AddSessionAsync(waitingForTheSelfie);
        await world.AddSessionAsync(noShowIsNotAsked, operatorRequested: true);
        await world.AddVerificationLogAsync(answered, BiometricOutcome.Escalate, byOperator: true);

        await using var db = world.Db();
        var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);

        foreach (var stay in new[] { asked, refused, attemptsUsed })
        {
            Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.OperatorDecisionRequired), (IdentityOf(status, stay).Light, IdentityOf(status, stay).Reason));
        }

        foreach (var stay in new[] { consentGivenAttemptsUsed, reviewExpired, answered, waitingForTheSelfie })
        {
            Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.SelfieAwaited), (IdentityOf(status, stay).Light, IdentityOf(status, stay).Reason));
        }

        Assert.DoesNotContain(status.Items, i => i.Kind == ComplianceItemKind.Identity && i.BookingId == noShowIsNotAsked.Id);
    }

    [Fact]
    public async Task ComplianceStatus_IdentityIsGreenOnlyWhenTheLatestRowOfTheLogIsAnAccept_Test()
    {
        var world = await World.SeedAsync();
        world.IdentityOn = true;
        var (operatorRejected, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0);
        var (operatorReopened, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0);
        var (automaticRejected, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0);
        var (automaticReview, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0);
        var (operatorApproved, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0);
        var (automaticAccepted, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0);
        var (acceptedThenReopened, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0);
        var (noRow, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0);
        await world.AddSessionAsync(operatorRejected, outcome: BiometricOutcome.Reject);
        await world.AddSessionAsync(operatorReopened, outcome: BiometricOutcome.Escalate);
        await world.AddSessionAsync(automaticRejected, outcome: BiometricOutcome.Reject);
        await world.AddSessionAsync(automaticReview, outcome: BiometricOutcome.Escalate);
        await world.AddSessionAsync(operatorApproved, outcome: BiometricOutcome.Accept);
        await world.AddSessionAsync(automaticAccepted, outcome: BiometricOutcome.Accept);
        await world.AddSessionAsync(acceptedThenReopened, outcome: BiometricOutcome.Escalate);
        await world.AddSessionAsync(noRow, outcome: BiometricOutcome.Accept);

        await world.AddVerificationLogAsync(operatorRejected, BiometricOutcome.Reject, byOperator: true);
        await world.AddVerificationLogAsync(operatorReopened, BiometricOutcome.Escalate, byOperator: true);
        await world.AddVerificationLogAsync(automaticRejected, BiometricOutcome.Reject, byOperator: false);
        await world.AddVerificationLogAsync(automaticReview, BiometricOutcome.Escalate, byOperator: false);
        await world.AddVerificationLogAsync(operatorApproved, BiometricOutcome.Accept, byOperator: true);
        await world.AddVerificationLogAsync(automaticAccepted, BiometricOutcome.Accept, byOperator: false);
        await world.AddVerificationLogAsync(acceptedThenReopened, BiometricOutcome.Accept, byOperator: false, createdAt: Start.UtcDateTime.AddHours(-2));
        await world.AddVerificationLogAsync(acceptedThenReopened, BiometricOutcome.Escalate, byOperator: true, createdAt: Start.UtcDateTime.AddHours(-1));

        await using var db = world.Db();
        var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);

        foreach (var stay in new[] { operatorRejected, operatorReopened, automaticRejected, automaticReview, acceptedThenReopened, noRow })
        {
            var verdict = IdentityOf(status, stay);
            Assert.NotEqual(ComplianceLight.Green, verdict.Light);
            Assert.Null(verdict.ProofReference);
            Assert.Null(verdict.LastVerifiedAt);
        }

        var approved = IdentityOf(status, operatorApproved);
        Assert.Equal((ComplianceLight.Green, ComplianceEvidenceSources.OperatorDecision, ComplianceReasonCodes.IdentityVerified), (approved.Light, approved.EvidenceSource, approved.Reason));
        var accepted = IdentityOf(status, automaticAccepted);
        Assert.Equal((ComplianceLight.Green, ComplianceEvidenceSources.BiometricLog), (accepted.Light, accepted.EvidenceSource));
        Assert.StartsWith("biometric-log:", accepted.ProofReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestedPhoto_HidesAnExistingUrgentTodoUntilAnotherHandOff()
    {
        var world = await World.SeedAsync();
        world.IdentityOn = true;
        var (stay, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0);
        await world.AddSessionAsync(stay, operatorRequested: true);
        await using var db = world.Db();
        var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock);
        async Task FeedAsync(ComplianceStatusResult status)
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            await new ComplianceActivityFeeder(activities, db, cache, NullLogger<ComplianceActivityFeeder>.Instance)
                .FeedAsync(world.OrgWide, status, CancellationToken.None);
        }
        await FeedAsync(await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None));
        var oldTodo = await db.InternalActivities.AsNoTracking().SingleAsync(a => a.EntityId == stay.Id && a.Action == ActivityAction.VerifyIdentity);

        await world.AddVerificationLogAsync(stay, BiometricOutcome.Escalate, byOperator: true);
        Assert.DoesNotContain(await activities.GetPendingAsync(world.OrgWide, CancellationToken.None), a => a.Id == oldTodo.Id);
        var waiting = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
        var verdict = IdentityOf(waiting, stay);
        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.SelfieAwaited, false), (verdict.Light, verdict.Reason, verdict.NeedsAttention));
        await FeedAsync(waiting);
        Assert.NotNull((await db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == oldTodo.Id)).CompletedAt);
        Assert.Empty(await db.InternalActivities.Where(a => a.EntityId == stay.Id && a.Action == ActivityAction.VerifyIdentity && a.CompletedAt == null).ToListAsync());

        var session = await db.GuestCheckInSessions.SingleAsync(s => s.BookingId == stay.Id);
        session.OperatorRequestedAt = Start.UtcDateTime.AddHours(2);
        await db.SaveChangesAsync();
        var reopened = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
        Assert.Equal(ComplianceReasonCodes.OperatorDecisionRequired, IdentityOf(reopened, stay).Reason);
        await FeedAsync(reopened);
        Assert.Contains(await activities.GetPendingAsync(world.OrgWide, CancellationToken.None), a => a.Action == ActivityAction.VerifyIdentity && a.EntityId == stay.Id && a.Id != oldTodo.Id);
    }

    [Fact]
    public async Task Property_ConnectionCredentialsAndCin_AreLightedFromWhatIsKnown()
    {
        var world = await World.SeedAsync();
        world.Senders[world.Granted.Id] = ComplianceSenderType.ChannelManager;
        world.Senders[world.Hidden.Id] = ComplianceSenderType.ChannelManager;
        world.Health[world.Granted.Id] = ConnectionHealthState.Inactive;
        world.Health[world.Hidden.Id] = ConnectionHealthState.Unknown;
        world.Granted.CinCode = "IT058091C27G5FFZDZ";
        await world.SaveAsync(world.Granted);
        await world.AddCredentialsAsync(world.Granted.Id, lastVerificationOk: false);
        await world.AddCredentialsAsync(world.Hidden.Id, lastVerificationOk: null);

        await using var db = world.Db();
        var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);

        var inactive = Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.Connection && i.PropertyId == world.Granted.Id);
        Assert.Equal((ComplianceCockpitAction.ActivateOctorateSend, ComplianceLight.Red, ComplianceReasonCodes.ConnectionInactive), (inactive.Action, inactive.Verdict.Light, inactive.Verdict.Reason));
        var unknown = Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.Connection && i.PropertyId == world.Hidden.Id);
        Assert.Equal(ComplianceLight.Yellow, unknown.Verdict.Light);

        var refused = Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.Credentials);
        Assert.Equal((world.Granted.Id, ComplianceCockpitAction.RenewPoliceCredentials, ComplianceReasonCodes.CredentialsRefused), (refused.PropertyId, refused.Action, refused.Verdict.Reason));

        var cins = status.Items.Where(i => i.Kind == ComplianceItemKind.Cin).ToDictionary(i => i.PropertyId);
        Assert.Equal((ComplianceLight.Green, "cin:IT058091C27G5FFZDZ"), (cins[world.Granted.Id].Verdict.Light, cins[world.Granted.Id].Verdict.ProofReference));
        Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.CinMissing), (cins[world.Hidden.Id].Verdict.Light, cins[world.Hidden.Id].Verdict.Reason));
    }

    [Fact]
    public async Task Periods_IstatNeedsTheRegionsDeadline_TheTaxNeedsBothReceipts_AndTheRateIsNeverPresumedZero()
    {
        var world = await World.SeedAsync();
        await world.AddComuneAsync("015146", regionIstatCode: "03");
        world.Granted.ComuneIstatCode = "015146";
        world.Granted.City = "Milano";
        await world.SaveAsync(world.Granted);

        await world.AddStayAsync(world.Granted, checkInOffset: -10, nights: 2, status: BookingStatus.CheckedOut, touristTax: 12m);
        await world.AddStayAsync(world.Granted, checkInOffset: -45, nights: 2, status: BookingStatus.CheckedOut, touristTax: 0m);

        // No region configured yet: ISTAT is «to define» and gives no item; the tax of October is owed and has no receipt; the rate is unknown.
        await using (var db = world.Db())
        {
            var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);

            Assert.DoesNotContain(status.Items, i => i.Kind == ComplianceItemKind.Istat);
            var tax = Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.TouristTax);
            Assert.Equal(("2026-10", ComplianceCockpitAction.DeclareTouristTax, ComplianceLight.Red, ComplianceReasonCodes.EvidenceMissing), (tax.Period, tax.Action, tax.Verdict.Light, tax.Verdict.Reason));
            var rate = Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.MunicipalityRate);
            Assert.Equal(ComplianceReasonCodes.MunicipalityRateMissing, rate.Verdict.Reason);
            Assert.Contains("2026-10", status.Evaluated.Periods);
            Assert.DoesNotContain("2026-11", status.Evaluated.Periods);
        }

        // The region of Milano has a deadline (day 10 of the next month): October and September have an ISTAT item, red once the deadline passed.
        world.Options.IstatDeadlineDays["LOM"] = 10;
        await world.AddProofAsync(world.Granted.Id, ManualProofKind.IstatProof, new DateOnly(2026, 10, 1));
        await world.AddProofAsync(world.Granted.Id, ManualProofKind.TouristTaxDeclaration, new DateOnly(2026, 10, 1));
        await world.AddProofAsync(world.Granted.Id, ManualProofKind.TouristTaxPayment, new DateOnly(2026, 10, 1));
        world.Rates.Add(new TouristTaxRate { City = "Milano", IsActive = true, EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), RatePerPersonPerNight = 3m });
        await using (var db = world.Db())
        {
            var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);

            var istat = status.Items.Where(i => i.Kind == ComplianceItemKind.Istat).ToDictionary(i => i.Period!);
            Assert.Equal(["2026-09", "2026-10"], istat.Keys.Order());
            Assert.Equal((ComplianceLight.Green, ComplianceReasonCodes.ReceiptArchived), (istat["2026-10"].Verdict.Light, istat["2026-10"].Verdict.Reason));
            Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.DeadlineOverdue), (istat["2026-09"].Verdict.Light, istat["2026-09"].Verdict.Reason));
            Assert.Equal(ComplianceCockpitAction.UploadIstatProof, istat["2026-09"].Action);

            var tax = Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.TouristTax);
            Assert.Equal(("2026-10", ComplianceLight.Green), (tax.Period, tax.Verdict.Light));
            Assert.DoesNotContain(status.Items, i => i.Kind == ComplianceItemKind.MunicipalityRate);
        }

        world.Rates.Clear();
        await world.AddDeclaredRateAsync("Milano");
        await using (var db = world.Db())
        {
            var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
            Assert.DoesNotContain(status.Items, i => i.Kind == ComplianceItemKind.MunicipalityRate);
        }
    }

    [Fact]
    public async Task Periods_ATouristTaxPaymentTheHostRecordedWithItsReceipt_IsTheProofOfTheMonthsItCovers()
    {
        var world = await World.SeedAsync();
        world.Granted.City = "Milano";
        await world.SaveAsync(world.Granted);
        await world.AddStayAsync(world.Granted, checkInOffset: -10, nights: 2, status: BookingStatus.CheckedOut, touristTax: 12m);
        await world.AddStayAsync(world.Granted, checkInOffset: -45, nights: 2, status: BookingStatus.CheckedOut, touristTax: 8m);

        await world.AddRemittanceAsync("Milano", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), withReceipt: false);
        await world.AddRemittanceAsync("Milano", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15), withReceipt: true);
        await world.AddRemittanceAsync("Bergamo", new DateOnly(2026, 7, 1), new DateOnly(2026, 10, 31), withReceipt: true);
        await using (var db = world.Db())
        {
            var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
            var tax = status.Items.Where(i => i.Kind == ComplianceItemKind.TouristTax).ToDictionary(i => i.Period!);
            Assert.Equal(["2026-09", "2026-10"], tax.Keys.Order());
            Assert.All(tax.Values, item => Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.EvidenceMissing), (item.Verdict.Light, item.Verdict.Reason)));
        }

        await world.AddRemittanceAsync("Milano", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), withReceipt: true);
        await world.AddRemittanceAsync("milano", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30), withReceipt: true);
        await using (var db = world.Db())
        {
            var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
            var tax = status.Items.Where(i => i.Kind == ComplianceItemKind.TouristTax).ToDictionary(i => i.Period!);
            foreach (var item in tax.Values)
            {
                Assert.Equal((ComplianceLight.Green, ComplianceReasonCodes.ReceiptArchived, ComplianceEvidenceSources.ManualUpload), (item.Verdict.Light, item.Verdict.Reason, item.Verdict.EvidenceSource));
                Assert.Contains("tourist-tax-remittance:", item.Verdict.ProofReference, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ClosedMonths_AreTheFullMonthsInsideTheLookback_BeforeTheMonthInProgress()
    {
        Assert.Equal(
            [new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)],
            ComplianceStatusService.ClosedMonths(new DateOnly(2026, 11, 6), 90));
        Assert.Equal(
            [new DateOnly(2026, 10, 1)],
            ComplianceStatusService.ClosedMonths(new DateOnly(2026, 11, 1), 31));
        Assert.Empty(ComplianceStatusService.ClosedMonths(new DateOnly(2026, 11, 6), 5));
    }

    private static ComplianceStatusItem ItemOf(ComplianceStatusResult status, Booking stay) =>
        Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.Alloggiati && i.BookingId == stay.Id);

    private static ComplianceVerdict IdentityOf(ComplianceStatusResult status, Booking stay) =>
        Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.Identity && i.BookingId == stay.Id).Verdict;

    internal sealed class World
    {
        private readonly string _database = Guid.NewGuid().ToString();

        public required OrgEntity Org { get; init; }

        public required User Owner { get; init; }

        public required string CollaboratorId { get; init; }

        public required Property Granted { get; init; }

        public required Property Hidden { get; init; }

        public required Property Foreign { get; init; }

        public FakeTimeProvider Clock { get; } = new(Start);

        public Dictionary<Guid, ComplianceSenderType> Senders { get; } = [];

        public Dictionary<Guid, ConnectionHealthState> Health { get; } = [];

        public List<TouristTaxRate> Rates { get; } = [];

        public ComplianceOptions Options { get; } = new();

        public bool ConnectorOn { get; set; } = true;

        public bool IdentityOn { get; set; }

        public HostScope Restricted => new(Org.Id, GrantedToUserId: CollaboratorId);

        public HostScope OrgWide => new(Org.Id);

        public AppDbContext Db() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

        public static async Task<World> SeedAsync()
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var org = HostScopeScenario.NewOrg("Host", $"host-{suffix}");
            var otherOrg = HostScopeScenario.NewOrg("Altro host", $"altro-{suffix}");
            var owner = HostScopeScenario.NewUser($"auth0|titolare-{suffix}", org.Id, UserRole.PropertyOwner);
            var collaborator = HostScopeScenario.NewUser($"auth0|collaboratore-{suffix}", org.Id, UserRole.None);
            var otherOwner = HostScopeScenario.NewUser($"auth0|altro-{suffix}", otherOrg.Id, UserRole.PropertyOwner);
            var granted = HostScopeScenario.NewProperty(org.Id, owner.Id, "Trullo");
            var hidden = HostScopeScenario.NewProperty(org.Id, owner.Id, "Casa Bianca");
            var foreign = HostScopeScenario.NewProperty(otherOrg.Id, otherOwner.Id, "Villa Altrove");

            var world = new World
            {
                Org = org,
                Owner = owner,
                CollaboratorId = collaborator.Id,
                Granted = granted,
                Hidden = hidden,
                Foreign = foreign,
            };
            await using var db = world.Db();
            db.Orgs.AddRange(org, otherOrg);
            db.Users.AddRange(owner, collaborator, otherOwner);
            db.Properties.AddRange(granted, hidden, foreign);
            db.OrgMembers.AddRange(
                new OrgMember { OrgId = org.Id, UserId = owner.Id, Role = OrgRole.Owner },
                new OrgMember { OrgId = org.Id, UserId = collaborator.Id, Role = OrgRole.Collaborator, PropertyScope = PropertyScope.Selected });
            db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = org.Id, UserId = collaborator.Id, PropertyId = granted.Id });
            await db.SaveChangesAsync();
            return world;
        }

        public async Task SaveAsync(Property property)
        {
            await using var db = Db();
            db.Properties.Update(property);
            await db.SaveChangesAsync();
        }

        public async Task UpdateStayAsync(Booking stay, Action<Booking> change)
        {
            await using var db = Db();
            var tracked = await db.Bookings.SingleAsync(b => b.Id == stay.Id);
            change(tracked);
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// The status service over the real context. The senders are the ones of <see cref="Senders"/> (a double that answers for every id it is
        /// asked) unless <paramref name="store"/> is given: then the real store reads them, as it does in production.
        /// </summary>
        public ComplianceStatusService StatusService(AppDbContext db, IPropertySenderStore? store = null)
        {
            IReadOnlyDictionary<Guid, PropertySenderInfo> SendersOf(IReadOnlyCollection<Guid> ids) =>
                ids.ToDictionary(
                    id => id,
                    id => new PropertySenderInfo(
                        id,
                        Org.Id,
                        Senders.GetValueOrDefault(id, ComplianceSenderType.Manual),
                        Senders.GetValueOrDefault(id, ComplianceSenderType.Manual),
                        SenderSource.Stored,
                        true,
                        null,
                        null));

            var senders = new Mock<IPropertySenderStore>();
            senders
                .Setup(s => s.GetManyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => SendersOf(ids));
            senders
                .Setup(s => s.GetManyForHistoryAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => SendersOf(ids));

            var health = new Mock<IComplianceConnectionHealthService>();
            health
                .Setup(h => h.GetManyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                    (IReadOnlyDictionary<Guid, ComplianceConnectionHealth>)ids.ToDictionary(id => id, id => HealthOf(id)));

            var quotes = new Mock<ITouristTaxQuoteService>();
            quotes
                .Setup(q => q.GetRatesInForceAsync(It.IsAny<TouristTaxComune>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((TouristTaxComune comune, DateOnly _, CancellationToken _) =>
                    (IReadOnlyList<TouristTaxRate>)Rates.Where(comune.Matches).ToList());

            var flags = new Mock<IFeatureFlags>();
            flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(() => ConnectorOn);
            flags.Setup(f => f.IsEnabled(FeatureFlags.CheckInIdentityVerification)).Returns(() => IdentityOn);

            return new ComplianceStatusService(
                db,
                store ?? senders.Object,
                health.Object,
                quotes.Object,
                flags.Object,
                Microsoft.Extensions.Options.Options.Create(Options),
                NullLogger<ComplianceStatusService>.Instance,
                Clock);
        }

        private ComplianceConnectionHealth HealthOf(Guid propertyId)
        {
            var state = Health.GetValueOrDefault(propertyId, ConnectionHealthState.Unknown);
            var line = new ConnectionHealthLine(ComplianceSenderType.ChannelManager, state, null, null, null, null, null);
            return new ComplianceConnectionHealth(propertyId, true, line, line, false, false, Start.UtcDateTime);
        }

        /// <summary>A stay (and its booker) with <paramref name="guests"/> registered guests and, when asked, its Alloggiati report.</summary>
        public async Task<(Booking Stay, AlloggiatiWebReport? Report)> AddStayAsync(
            Property property,
            int checkInOffset,
            int nights = 2,
            int guests = 0,
            BookingStatus status = BookingStatus.Confirmed,
            bool noShow = false,
            AlloggiatiWebStatus? report = null,
            DateTime? reportedAt = null,
            decimal touristTax = 0m)
        {
            await using var db = Db();
            var guest = new Guest { OrgId = property.OrgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
            var stay = new Booking
            {
                OrgId = property.OrgId,
                PropertyId = property.Id,
                GuestId = guest.Id,
                CheckInDate = Day(checkInOffset),
                CheckOutDate = Day(checkInOffset + nights),
                NumberOfGuests = Math.Max(1, guests),
                Status = status,
                Source = BookingSource.Manual,
                BasePrice = 300m,
                TotalPrice = 300m,
                TouristTax = touristTax,
                NoShowAt = noShow ? Day(checkInOffset + 1) : null,
            };
            db.Guests.Add(guest);
            db.Bookings.Add(stay);
            for (var position = 0; position < guests; position++)
                db.StayGuests.Add(new StayGuest { OrgId = property.OrgId, BookingId = stay.Id, Position = position, FirstName = "Ospite", LastName = $"{position}" });

            AlloggiatiWebReport? row = null;
            if (report is { } reportStatus)
            {
                row = new AlloggiatiWebReport
                {
                    OrgId = property.OrgId,
                    BookingId = stay.Id,
                    GuestId = guest.Id,
                    Status = reportStatus,
                    ReportedAt = reportedAt ?? Start.UtcDateTime.AddDays(checkInOffset),
                    UpdatedAt = Start.UtcDateTime,
                };
                if (reportStatus == AlloggiatiWebStatus.Inviato)
                    row.ConfirmationNumber = "TEST-0001";
                db.AlloggiatiWebReports.Add(row);
            }

            await db.SaveChangesAsync();
            return (stay, row);
        }

        public async Task EntrustAsync(Booking stay, int[] positions, bool asDraft = false, OctorateSyncStatus status = OctorateSyncStatus.Sent)
        {
            await using var db = Db();
            foreach (var position in positions)
            {
                db.StayGuestOctorateLinks.Add(new StayGuestOctorateLink
                {
                    OrgId = stay.OrgId,
                    BookingId = stay.Id,
                    Position = position,
                    OctorateGuestId = $"TEST-{position}",
                    PayloadHash = new string('0', StayGuestOctorateLink.PayloadHashLength),
                    SentAsDraft = asDraft,
                    Status = status,
                    SentAt = Start.UtcDateTime.AddHours(-1),
                });
            }

            await db.SaveChangesAsync();
        }

        public async Task AddReceiptAsync(AlloggiatiWebReport report, AlloggiatiMode mode)
        {
            await using var db = Db();
            var receipt = new AlloggiatiReceiptEntity
            {
                OrgId = report.OrgId,
                AccountKey = Guid.NewGuid(),
                ReceiptDate = Day(-3),
                Mode = mode,
                StorageKey = $"alloggiati/test/{Guid.NewGuid():N}.pdf",
                ContentHash = new string('a', AlloggiatiReceiptEntity.ContentHashLength),
                SizeBytes = 100,
                RetrievedAt = Start.UtcDateTime,
                RetainUntil = Start.UtcDateTime.AddYears(5),
            };
            db.AlloggiatiReceipts.Add(receipt);
            var tracked = await db.AlloggiatiWebReports.SingleAsync(r => r.Id == report.Id);
            tracked.ReceiptId = receipt.Id;
            await db.SaveChangesAsync();
        }

        public async Task AddProofAsync(Guid propertyId, ManualProofKind kind, DateOnly day)
        {
            await using var db = Db();
            db.ManualComplianceProofs.Add(new ManualComplianceProof
            {
                OrgId = Org.Id,
                PropertyId = propertyId,
                Kind = kind,
                Day = day,
                FileKey = $"compliance/test/{Guid.NewGuid():N}.pdf",
                ContentType = "application/pdf",
                UploadedByUserId = Owner.Id,
                UploadedAt = Start.UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        public async Task AddRemittanceAsync(string comune, DateOnly from, DateOnly to, bool withReceipt)
        {
            await using var db = Db();
            var id = Guid.NewGuid();
            db.TouristTaxRemittances.Add(new TouristTaxRemittance
            {
                Id = id,
                OrgId = Org.Id,
                Comune = comune,
                ComuneKey = TouristTaxComune.NormalizeName(comune),
                PeriodFrom = from,
                PeriodTo = to,
                PaidAmount = 20m,
                PaidOn = to,
                RecordedByUserId = Owner.Id,
                ReceiptStorageKey = withReceipt ? $"tourist-tax/{Org.Id}/{id}/ricevuta.pdf" : null,
                ReceiptFileName = withReceipt ? "ricevuta.pdf" : null,
                ReceiptContentType = withReceipt ? "application/pdf" : null,
            });
            await db.SaveChangesAsync();
        }

        public async Task AddCredentialsAsync(Guid propertyId, bool? lastVerificationOk)
        {
            await using var db = Db();
            db.PropertyQuesturaCredentials.Add(new PropertyQuesturaCredentials
            {
                OrgId = Org.Id,
                PropertyId = propertyId,
                Username = "u",
                Password = "p",
                WsKey = "k",
                LastVerificationOk = lastVerificationOk,
            });
            await db.SaveChangesAsync();
        }

        public async Task AddSessionAsync(
            Booking stay,
            BiometricOutcome? outcome = null,
            int? attempts = null,
            bool consentRefused = false,
            bool operatorRequested = false,
            bool expired = false,
            bool consentGiven = false)
        {
            await using var db = Db();
            db.GuestCheckInSessions.Add(new GuestCheckInSession
            {
                OrgId = stay.OrgId,
                BookingId = stay.Id,
                TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                ExpiresAt = Start.UtcDateTime.AddDays(10),
                BiometricOutcome = outcome,
                BiometricAttempts = attempts ?? (outcome is null ? 0 : 1),
                BiometricConsentAt = consentGiven ? Start.UtcDateTime.AddHours(-4) : null,
                BiometricConsentRefusedAt = consentRefused ? Start.UtcDateTime.AddHours(-4) : null,
                OperatorRequestedAt = operatorRequested ? Start.UtcDateTime.AddHours(-3) : null,
                Status = expired ? GuestCheckInSessionStatus.Scaduto : GuestCheckInSessionStatus.Inviato,
                CreatedAt = Start.UtcDateTime.AddHours(-5),
            });
            await db.SaveChangesAsync();
        }

        public async Task AddVerificationLogAsync(Booking stay, BiometricOutcome outcome, bool byOperator, DateTime? createdAt = null)
        {
            await using var db = Db();
            if (byOperator && !await db.Users.AnyAsync(u => u.Id == "auth0|operatore"))
                db.Users.Add(HostScopeScenario.NewUser("auth0|operatore", Org.Id, UserRole.None));

            db.BiometricVerificationLogs.Add(new BiometricVerificationLog
            {
                OrgId = stay.OrgId,
                BookingId = stay.Id,
                GuestId = stay.GuestId,
                Outcome = outcome,
                Method = byOperator ? VerificationMethod.Operator : VerificationMethod.Biometric,
                Attempts = 1,
                ModelName = "TEST",
                ModelVersion = "1",
                OperatorUserId = byOperator ? "auth0|operatore" : null,
                CreatedAt = createdAt ?? Start.UtcDateTime.AddHours(1),
            });
            await db.SaveChangesAsync();
        }

        public async Task AddComuneAsync(string istatCode, string regionIstatCode)
        {
            await using var db = Db();
            db.Comuni.Add(new Comune
            {
                IstatCode = istatCode,
                Name = "Milano",
                DisplayName = "Milano",
                NormalizedName = "milano",
                SearchText = "milano",
                ProvinceCode = "MI",
                RegionIstatCode = regionIstatCode,
                RegionName = "Lombardia",
            });
            await db.SaveChangesAsync();
        }

        public async Task AddDeclaredRateAsync(string city)
        {
            await using var db = Db();
            db.OrgTouristTaxRates.Add(new OrgTouristTaxRate
            {
                OrgId = Org.Id,
                City = city,
                RatePerPersonPerNight = 2m,
                EffectiveFrom = new DateOnly(2026, 1, 1),
                SourceNote = "TEST",
                EnteredByUserId = Owner.Id,
            });
            await db.SaveChangesAsync();
        }
    }
}
