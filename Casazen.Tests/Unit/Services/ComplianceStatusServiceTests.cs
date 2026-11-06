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
    // 2026-11-06 11:00 in Rome.
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    private static DateTime Day(int offsetFromToday) => new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc).AddDays(offsetFromToday);

    // ─── The reach of the caller ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ComplianceStatus_RespectsHostScope_Test()
    {
        var world = await World.SeedAsync();
        var (grantedStay, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2);
        var (hiddenStay, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2);
        var (foreignStay, _) = await world.AddStayAsync(world.Foreign, checkInOffset: -2);

        await using var db = world.Db();
        var service = world.StatusService(db);

        // The collaborator «Solo alcuni» was given the first property only: every light is of that property and of its stays.
        var restricted = await service.EvaluateAsync(world.Restricted, CancellationToken.None);
        Assert.NotEmpty(restricted.Items);
        Assert.All(restricted.Items, item => Assert.Equal(world.Granted.Id, item.PropertyId));
        Assert.Equal([grantedStay.Id], restricted.Items.Where(i => i.BookingId is not null).Select(i => i.BookingId!.Value));
        Assert.Equal([world.Granted.Id], restricted.Evaluated.PropertyIds);
        Assert.Equal([grantedStay.Id], restricted.Evaluated.BookingIds);

        // The whole org sees both, and never the stays of another org.
        var orgWide = await service.EvaluateAsync(world.OrgWide, CancellationToken.None);
        Assert.Equal(
            new[] { grantedStay.Id, hiddenStay.Id }.Order(),
            orgWide.Items.Where(i => i.BookingId is not null).Select(i => i.BookingId!.Value).Order());
        Assert.DoesNotContain(orgWide.Items, i => i.PropertyId == world.Foreign.Id || i.BookingId == foreignStay.Id);
        Assert.Equal(new[] { world.Granted.Id, world.Hidden.Id }.Order(), orgWide.Evaluated.PropertyIds.Order());

        // The To do the feeder raises from the collaborator's status are those of its property alone.
        var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock);
        var feeder = new ComplianceActivityFeeder(activities, new MemoryCache(new MemoryCacheOptions()), NullLogger<ComplianceActivityFeeder>.Instance);
        await feeder.FeedAsync(world.Restricted, restricted, CancellationToken.None);

        var raised = await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        Assert.NotEmpty(raised);
        Assert.All(raised, a => Assert.Equal(world.Granted.Id, a.PropertyId));
        Assert.DoesNotContain(raised, a => a.EntityId == hiddenStay.Id || a.EntityId == world.Hidden.Id || a.EntityId == foreignStay.Id);
        Assert.Contains(raised, a => a.EntityType == InternalActivity.BookingEntityType && a.EntityId == grantedStay.Id);

        // And the whole org's status raises the hidden property's, once the whole org is the caller.
        await feeder.FeedAsync(world.OrgWide, orgWide, CancellationToken.None);
        Assert.Contains(await db.InternalActivities.AsNoTracking().ToListAsync(), a => a.EntityId == hiddenStay.Id);
    }

    // ─── Handoff ────────────────────────────────────────────────────────────────────────────────────────

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

            // The host starts the handoff for the stays and comes back: the passage is a line of the activity log and nothing else.
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

    // ─── Alloggiati: proofs, entrusting, the stays that count ───────────────────────────────────────────

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
        // The proof of the host is filed under the property and the arrival day: another property's, or another day's, proves nothing here.
        await world.AddProofAsync(world.Hidden.Id, ManualProofKind.AlloggiatiReceipt, DateOnly.FromDateTime(Day(-2)));
        await world.AddProofAsync(world.Hidden.Id, ManualProofKind.AlloggiatiReceipt, DateOnly.FromDateTime(Day(-5)));
        await world.AddProofAsync(world.Hidden.Id, ManualProofKind.TouristTaxPayment, DateOnly.FromDateTime(Day(-1)));

        await using var db = world.Db();
        var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);

        var live = ItemOf(status, viaReceipt).Verdict;
        Assert.Equal((ComplianceLight.Green, ComplianceReasonCodes.ReceiptArchived, ComplianceEvidenceSources.CasazenReceipt), (live.Light, live.Reason, live.EvidenceSource));
        Assert.StartsWith("receipt:", live.ProofReference, StringComparison.Ordinal);
        // A simulated receipt never stands for a real one: the stay is sent, with no receipt, three days ago.
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

        // The host uploads the receipt of the day: both turn green.
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
        foreach (var notEntrusted in new[] { some, draft, failed, none })
        {
            // Not entrusted: the host still has to send it (a link that is a draft, failed or missing is not a send).
            Assert.Equal((ComplianceCockpitAction.SendAlloggiati, ComplianceLight.Red), (ItemOf(status, notEntrusted).Action, ItemOf(status, notEntrusted).Verdict.Light));
        }

        // Over 30 nights the channel manager does not send, entrusted or not.
        Assert.Equal((ComplianceCockpitAction.SendAlloggiatiManually, ComplianceReasonCodes.OverThirtyNightsNotSent),
            (ItemOf(status, longStay).Action, ItemOf(status, longStay).Verdict.Reason));

        // With the connector off nothing is entrusted to the channel manager and the host sends: the same stays are all to send.
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

    // ─── Identity ───────────────────────────────────────────────────────────────────────────────────────

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

        // Off (the default): the identity of nobody is asked about.
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
        Assert.Equal((ComplianceLight.Green, ComplianceEvidenceSources.OperatorDecision), (IdentityOf(status, decided).Light, IdentityOf(status, decided).EvidenceSource));
        Assert.DoesNotContain(status.Items, i => i.Kind == ComplianceItemKind.Identity && i.BookingId == noSession.Id);
        Assert.Contains(noSession.Id, status.Evaluated.IdentityBookingIds);
    }

    // ─── Properties: connection, credentials, CIN, rate, periods ────────────────────────────────────────

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

        // A stay in October with a tax recorded, and one in September with none, both finished.
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
            // The months looked at: the closed ones inside the lookback (the month in progress is not one).
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

            // Both receipts of October's tax are archived; September owes nothing (the one stay carries no tax); the rate is known.
            var tax = Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.TouristTax);
            Assert.Equal(("2026-10", ComplianceLight.Green), (tax.Period, tax.Verdict.Light));
            Assert.DoesNotContain(status.Items, i => i.Kind == ComplianceItemKind.MunicipalityRate);
        }

        // A rate the host declared for the comune counts as known.
        world.Rates.Clear();
        await world.AddDeclaredRateAsync("Milano");
        await using (var db = world.Db())
        {
            var status = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
            Assert.DoesNotContain(status.Items, i => i.Kind == ComplianceItemKind.MunicipalityRate);
        }
    }

    [Fact]
    public void ClosedMonths_AreTheFullMonthsInsideTheLookback_BeforeTheMonthInProgress()
    {
        // 90 days before 2026-11-06 is 2026-08-08: August is cut by the lookback, September and October are whole, November is in progress.
        Assert.Equal(
            [new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)],
            ComplianceStatusService.ClosedMonths(new DateOnly(2026, 11, 6), 90));
        // The lookback starts on the 1st: that month counts.
        Assert.Equal(
            [new DateOnly(2026, 10, 1)],
            ComplianceStatusService.ClosedMonths(new DateOnly(2026, 11, 1), 31));
        Assert.Empty(ComplianceStatusService.ClosedMonths(new DateOnly(2026, 11, 6), 5));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    private static ComplianceStatusItem ItemOf(ComplianceStatusResult status, Booking stay) =>
        Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.Alloggiati && i.BookingId == stay.Id);

    private static ComplianceVerdict IdentityOf(ComplianceStatusResult status, Booking stay) =>
        Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.Identity && i.BookingId == stay.Id).Verdict;

    /// <summary>
    /// One org with a collaborator «Solo alcuni» who was given the first property, a hidden property, and a property of another org; the
    /// services under test over the real context, with the senders, the connection health, the rates and the flags under the test's control.
    /// </summary>
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

        public ComplianceStatusService StatusService(AppDbContext db)
        {
            var senders = new Mock<IPropertySenderStore>();
            senders
                .Setup(s => s.GetManyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                    (IReadOnlyDictionary<Guid, PropertySenderInfo>)ids.ToDictionary(
                        id => id,
                        id => new PropertySenderInfo(
                            id,
                            Org.Id,
                            Senders.GetValueOrDefault(id, ComplianceSenderType.Manual),
                            Senders.GetValueOrDefault(id, ComplianceSenderType.Manual),
                            SenderSource.Stored,
                            true,
                            null,
                            null)));

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
                senders.Object,
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

        public async Task AddSessionAsync(Booking stay, BiometricOutcome? outcome = null)
        {
            await using var db = Db();
            db.GuestCheckInSessions.Add(new GuestCheckInSession
            {
                OrgId = stay.OrgId,
                BookingId = stay.Id,
                TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                ExpiresAt = Start.UtcDateTime.AddDays(10),
                BiometricOutcome = outcome,
                BiometricAttempts = outcome is null ? 0 : 1,
            });
            await db.SaveChangesAsync();
        }

        public async Task AddVerificationLogAsync(Booking stay, BiometricOutcome outcome, bool byOperator)
        {
            await using var db = Db();
            if (byOperator)
                db.Users.Add(HostScopeScenario.NewUser("auth0|operatore", Org.Id, UserRole.None));

            db.BiometricVerificationLogs.Add(new BiometricVerificationLog
            {
                OrgId = stay.OrgId,
                BookingId = stay.Id,
                GuestId = stay.GuestId,
                Outcome = outcome,
                Attempts = 1,
                ModelName = "TEST",
                ModelVersion = "1",
                OperatorUserId = byOperator ? "auth0|operatore" : null,
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
