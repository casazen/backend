using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-BKP, decision of the product owner of 2026-10-29/30: a host's request goes to the supplier it chose (the primary) and, at the same
/// moment, to up to three backups, declared as such. The primary has one hour; the first backup that accepts has the job if it does not.
/// One test for each rule that decides who has the job, who sees what and who is told. The locks and the xmin checks need PostgreSQL
/// (<c>ServiceRequestGroupPostgresTests</c>); here the calls are one after the other, which is what the rules are about.
/// </summary>
public class ServiceRequestBackupGroupTests
{
    private static readonly TimeSpan PrimaryWindow = TimeSpan.FromMinutes(60);

    /// <summary>A backup of a group: its request and the supplier org it was sent to.</summary>
    private sealed record BackupSide(ServiceRequest Request, Guid SupplierOrgId)
    {
        public Guid Id => Request.Id;

        public string Member => $"auth0|member-of-{SupplierOrgId:N}";
    }

    // ─── Creation ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithTheFlagOn_SendsTheRequestToTheBackupsToo_DeclaredAsSuch_AndTheyHoldNoSlot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (alfa, beta) = await AddTwoBackupsAsync(s);
        s.ForgetNotifications();

        var primary = await s.RequestAsync(withService: false);

        var group = await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync();
        Assert.Equal(s.HostOrgId, group.OrgId);
        Assert.Equal(ServiceRequestGroupPhase.PrimaryWindow, group.Phase);
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime + PrimaryWindow, group.PrimaryDueAt);
        Assert.Null(group.BackupDueAt);

        var savedPrimary = await s.ReadAsync(primary.Id);
        Assert.Equal(group.Id, savedPrimary.GroupId);
        Assert.Equal(ServiceRequestGroupRole.Primary, savedPrimary.GroupRole);
        // The primary has the hour of the rule, not the 120 minutes of a request sent to one supplier.
        Assert.Equal(group.PrimaryDueAt, savedPrimary.ResponseDueAt);

        var backups = await BackupsOfAsync(s, group.Id);
        Assert.Equal(new[] { alfa, beta }.Order(), backups.Select(r => r.SupplierOrgId).Order());
        Assert.All(backups, backup =>
        {
            Assert.Equal(ServiceRequestStatus.Richiesto, backup.Status);
            Assert.Equal(group.Id, backup.GroupId);
            // The same job: host, property, stay, notes, time.
            Assert.Equal(s.HostOrgId, backup.OrgId);
            Assert.Equal(s.PropertyId, backup.PropertyId);
            Assert.Equal(s.BookingId, backup.BookingId);
            Assert.Equal(ServiceRequestScenario.HostNotes, backup.Notes);
            Assert.Equal(savedPrimary.ScheduledStartUtc, backup.ScheduledStartUtc);
            Assert.Equal(savedPrimary.ScheduledEndUtc, backup.ScheduledEndUtc);
            Assert.Null(backup.BackupAcceptedAt);
            Assert.Equal(group.PrimaryDueAt, backup.ResponseDueAt);
        });

        // A backup holds nothing of its agenda until it has the job; the primary's supplier does hold it (a request waiting counts).
        var day = RomeCalendar.DateInRome(savedPrimary.ScheduledStartUtc!.Value);
        Assert.Empty(await s.Reader.ListForAgendaAsync(alfa, day, day));
        Assert.Empty(await s.Reader.ListForAgendaAsync(beta, day, day));
        Assert.Single(await s.Reader.ListForAgendaAsync(s.SupplierOrgId, day, day));
    }

    [Fact]
    public async Task Create_TheBackupsAreTold_TheyAreBackups_WithTheComuneAndNeverThePropertyOrTheHostNotes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierRequestBackups, true);
        var alfa = await AddBackupAsync(s, "Alfa Srl");
        s.ForgetNotifications();

        var primary = await s.RequestAsync(withService: false);

        var invitation = Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ServiceRequestBackupInvited);
        Assert.Equal("alfa-srl@test.com", invitation.To);
        var text = invitation.Content.Subject + invitation.Content.HtmlBody;
        Assert.Contains("fornitore di riserva", text);
        Assert.Contains(ServiceRequestScenario.Comune, text);
        Assert.DoesNotContain(ServiceRequestScenario.PropertyName, text);
        Assert.DoesNotContain(ServiceRequestScenario.PropertyAddress, text);
        Assert.DoesNotContain(ServiceRequestScenario.HostNotes, text);

        var backup = Assert.Single(await BackupsOfAsync(s, (await s.ReadAsync(primary.Id)).GroupId!.Value));
        var push = Assert.Single(s.Pushes, p => p.Payload.Type == PushTypes.ServiceRequestBackupInvited);
        Assert.Equal(PushAudience.SupplierOrg(alfa), push.Audience);
        // The tap opens the request in the supplier area of the app, not the host's property list.
        Assert.Equal(PushRoutes.SupplierRequest(backup.Id), push.Payload.Route);
        Assert.Equal(PushDeliveryKeys.ServiceRequestBackup(backup.Id, "invited"), push.DeliveryKey);
        // The chosen supplier still gets the email of a new request, and is not told about the backups.
        Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com" && e.Template == EmailTemplates.Names.ServiceRequestCreated);
    }

    [Fact]
    public async Task Create_WithTheFlagOffTheOrgOffOrTheRequestOff_OrNobodyElseCoversTheComune_GoesToTheChosenSupplierAlone()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var window = TimeSpan.FromMinutes(new Casazen.Core.Options.ServiceRequestOptions().HostResponseMinutes);

        // Nobody else in the comune: with the flag on, a single request, with the deadline of today.
        s.Flags.Set(FeatureFlags.SupplierRequestBackups, true);
        await AssertSentAloneAsync(s, (await s.RequestAsync(withService: false)).Id, window);

        await AddBackupAsync(s, "Alfa Srl");

        // The flag is off.
        s.Flags.Set(FeatureFlags.SupplierRequestBackups, false);
        await AssertSentAloneAsync(s, (await s.RequestAsync(withService: false)).Id, window);

        // The flag is on, the org turned the rule off.
        s.Flags.Set(FeatureFlags.SupplierRequestBackups, true);
        var org = await s.Db.Orgs.SingleAsync(o => o.Id == s.HostOrgId);
        org.SupplierBackupsEnabled = false;
        await s.Db.SaveChangesAsync();
        await AssertSentAloneAsync(s, (await CreateAsync(s, useBackups: null)).Id, window);

        // The org turned it on again and this request asks to be sent alone.
        org.SupplierBackupsEnabled = true;
        await s.Db.SaveChangesAsync();
        await AssertSentAloneAsync(s, (await CreateAsync(s, useBackups: false)).Id, window);

        // Control: the same, asking for the backups, forms a group.
        var grouped = await CreateAsync(s, useBackups: null);
        Assert.NotNull((await s.ReadAsync(grouped.Id)).GroupId);
    }

    [Fact]
    public async Task Create_WhenTheHostPickedAPublishedSlot_OnlyTheBackupsThatHaveThatSlotFreeAreChosen()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierRequestBackups, true);
        var withoutHours = await AddBackupAsync(s, "Alfa Srl");
        var withHours = await AddBackupAsync(s, "Beta Srl");
        await s.Agenda.ReplaceRulesAsync(withHours, new SupplierRulesInput(30, 3, 0, 35, 60));
        await s.Agenda.ReplaceHoursAsync(withHours, ServiceRequestScenario.WorkingWeek());

        // The slot is one of the primary's published slots (a catalog service, no typed duration): each backup must have it too.
        var primary = await s.RequestAsync(ServiceRequestScenario.FridayAt10);

        var backups = await BackupsOfAsync(s, (await s.ReadAsync(primary.Id)).GroupId!.Value);
        Assert.Equal(withHours, Assert.Single(backups).SupplierOrgId);
        Assert.DoesNotContain(backups, backup => backup.SupplierOrgId == withoutHours);
        Assert.True((await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).SlotPlanned);
    }

    // ─── What the host sees ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Host_SeesOnlyTheRequestItFollows_TheGroupWithEveryBackup_AndCannotActOnABackup()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);
        var scope = new HostScope(s.HostOrgId);

        var (items, total) = await s.Service.ListForHostAsync(scope, ServiceRequestRentalContext.ShortRent, null, null, null, 1, 50);

        Assert.Equal(1, total);
        Assert.Equal(primary.Id, Assert.Single(items).Id);
        Assert.Null(await s.Service.GetByIdForHostAsync(alfa.Id, scope, ServiceRequestRentalContext.ShortRent));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.CancelAsHostAsync(alfa.Id, s.HostOrgId, "no"));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.RemindAsync(alfa.Id, s.HostOrgId));

        var group = Assert.Single(await s.Service.GetGroupViewsAsync(s.HostOrgId, [primary.GroupId!.Value])).Value;
        Assert.Equal(ServiceRequestGroupPhase.PrimaryWindow, group.Phase);
        Assert.Equal(ServiceRequestGroupRole.Primary, group.Members[0].Role);
        Assert.Equal("Supplier Org", group.Members[0].SupplierName);
        Assert.Equal(2, group.Members.Count(m => m.Role == ServiceRequestGroupRole.Backup));
        Assert.Equal(["Alfa Srl", "Beta Srl"], group.Members.Where(m => m.Role == ServiceRequestGroupRole.Backup).Select(m => m.SupplierName).Order());
        Assert.All(group.Members, member => Assert.Equal(ServiceRequestStatus.Richiesto, member.Status));
        // Another org does not read the group.
        Assert.Empty(await s.Service.GetGroupViewsAsync(Guid.NewGuid(), [primary.GroupId.Value]));
    }

    // ─── The primary takes the job ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PrimaryTakesTheJob_TheBackupsAreClosed_AndToldSo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        s.ForgetNotifications();

        var taken = await s.Service.TakeAsync(primary.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        var group = await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync();
        Assert.Equal(ServiceRequestGroupPhase.Closed, group.Phase);
        Assert.NotNull(group.ClosedAt);
        foreach (var backup in new[] { alfa, beta })
        {
            var saved = await s.ReadAsync(backup.Id);
            Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
            Assert.Equal(ServiceRequestActorParty.System, saved.CancelledBy);
            Assert.Equal(ServiceRequestCancellationReasons.PrimaryTookIt, saved.CancellationReason);
            Assert.Equal(ServiceRequestGroupRole.Backup, saved.GroupRole);
        }

        Assert.Equal(2, s.Emails.Snapshot().Count(e => e.Template == EmailTemplates.Names.ServiceRequestBackupClosed));
        // The host is told of the take like for any request, not of backups.
        Assert.Single(s.Emails.Snapshot(), e => e.To == "host@test.com" && e.Template == EmailTemplates.Names.ServiceRequestStatusChanged);
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ServiceRequestTakenByBackup);
    }

    [Fact]
    public async Task ThePrimaryThatTakesTheJobAfterItsHour_IsStillInTheRace_WhenNoBackupAccepted()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);
        s.Clock.Advance(PrimaryWindow);

        var late = await s.Service.TakeAsync(primary.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, late.Status);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(alfa.Id)).Status);
    }

    [Fact]
    public async Task ThePrimaryThatTakesTheJobAfterItsHour_IsRefused_WhenABackupHoldsAPreApproval()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);
        s.Clock.Advance(PrimaryWindow);

        // The pass of the job has not run yet: the rule is the same, the primary cannot take the job from the backup that accepted in time.
        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.TakeAsync(primary.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId));

        Assert.Equal(ServiceRequestGroupErrorCodes.PrimaryWindowPassed, refused.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(primary.Id)).Status);
    }

    // ─── The primary is silent for an hour ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PrimarySilentForAnHour_TheJobGoesToTheBackupThatAcceptedFirst_AndTheHostSeesItInPlaceOfThePrimary()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);

        // Beta accepts first, and that closes the race: Alfa, which would have accepted three minutes later, finds it closed.
        s.Clock.Advance(TimeSpan.FromMinutes(2));
        await s.Service.AcceptAsBackupAsync(beta.Id, beta.SupplierOrgId, beta.Member);
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        var tooLate = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member));
        Assert.Equal(ServiceRequestGroupErrorCodes.BackupRaceClosed, tooLate.Code);

        // Before the end of the hour nothing happens.
        s.Clock.Advance(TimeSpan.FromMinutes(54));
        Assert.Equal(ServiceRequestGroupRun.Empty, await Job(s).RunAsync());

        s.Clock.Advance(TimeSpan.FromMinutes(1));
        s.ForgetNotifications();
        var run = await Job(s).RunAsync();

        Assert.Equal(new ServiceRequestGroupRun(false, 1, 1, 0, 0, 0), run);
        var winner = await s.ReadAsync(beta.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, winner.Status);
        Assert.Equal(ServiceRequestGroupRole.PromotedBackup, winner.GroupRole);
        Assert.Equal(beta.Member, winner.TakenByUserId);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, winner.TakenAt);
        Assert.Null(winner.ResponseDueAt);
        // Alfa was closed when Beta accepted, not by the take: its request says so, and it was told then.
        var other = await s.ReadAsync(alfa.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, other.Status);
        Assert.Equal(ServiceRequestCancellationReasons.BackupRaceClosed, other.CancellationReason);
        var lost = await s.ReadAsync(primary.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, lost.Status);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, lost.CancellationReason);
        Assert.Equal(ServiceRequestActorParty.System, lost.CancelledBy);
        Assert.Equal(ServiceRequestGroupRole.ReplacedPrimary, lost.GroupRole);
        Assert.Equal(ServiceRequestGroupPhase.Closed, (await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).Phase);

        // The host follows the backup that took the job, in the place of the primary, and is told who it is.
        var (items, _) = await s.Service.ListForHostAsync(new HostScope(s.HostOrgId), ServiceRequestRentalContext.ShortRent, null, null, null, 1, 50);
        Assert.Equal(beta.Id, Assert.Single(items).Id);
        var toHost = Assert.Single(s.Emails.Snapshot(), e => e.To == "host@test.com");
        Assert.Equal(EmailTemplates.Names.ServiceRequestTakenByBackup, toHost.Template);
        Assert.Contains("Beta Srl", toHost.Content.HtmlBody);

        // The winner is told the job is its; the primary that did not answer is told its own, and why it loses points. The other backup was
        // told when the race closed, and is not told again.
        Assert.Single(s.Emails.Snapshot(), e => e.To == "beta-srl@test.com" && e.Template == EmailTemplates.Names.ServiceRequestBackupWon);
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.To == "alfa-srl@test.com");
        var toPrimary = Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com" && e.Template == EmailTemplates.Names.ServiceRequestCancelledToSupplier);
        Assert.Contains("punteggio di affidabilità", toPrimary.Content.HtmlBody);

        // Reliability (SP-REL): Beta answered in time as a backup, the primary missed its hour, Alfa lost nothing.
        var betaEvents = await s.ReliabilityEventsAsync(beta.SupplierOrgId);
        var answered = Assert.Single(betaEvents);
        Assert.Equal(SupplierReliabilityEventKind.RepliedInTime, answered.Kind);
        Assert.True(answered.AsBackup);
        Assert.Empty(await s.ReliabilityEventsAsync(alfa.SupplierOrgId));
        var missed = Assert.Single(await s.ReliabilityEventsAsync());
        Assert.Equal(SupplierReliabilityEventKind.NoReplyInTime, missed.Kind);
        Assert.False(missed.AsBackup);
    }

    [Fact]
    public async Task PrimarySilent_TheBackupThatAcceptedButWhoseTimeIsGoneMeanwhile_IsDropped_AndThePrimaryGetsTheSecondWindow()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);

        // Alfa takes another job at that time before the hour is over (written straight to the table: the planner would have refused it).
        var savedPrimary = await s.ReadAsync(primary.Id);
        await s.SeedAsync(
            ServiceRequestStatus.PresoInCarico,
            r =>
            {
                r.ScheduledStartUtc = savedPrimary.ScheduledStartUtc;
                r.ScheduledEndUtc = savedPrimary.ScheduledEndUtc;
                r.TakenAt = ServiceRequestScenario.Instant.UtcDateTime;
            },
            supplierOrgId: alfa.SupplierOrgId);
        s.Clock.Advance(PrimaryWindow);
        s.ForgetNotifications();

        var run = await Job(s).RunAsync();

        // Beta was closed with the race and cannot have the job: nobody is left but the primary, which has a second window without priority.
        Assert.Equal(new ServiceRequestGroupRun(false, 1, 0, 1, 0, 0), run);
        var dropped = await s.ReadAsync(alfa.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, dropped.Status);
        Assert.Equal(ServiceRequestCancellationReasons.BackupSlotUnavailable, dropped.CancellationReason);
        Assert.Equal(ServiceRequestCancellationReasons.BackupRaceClosed, (await s.ReadAsync(beta.Id)).CancellationReason);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(primary.Id)).Status);
        Assert.Equal(ServiceRequestGroupPhase.BackupWindow, (await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).Phase);
        var toAlfa = Assert.Single(s.Emails.Snapshot(), e => e.To == "alfa-srl@test.com");
        Assert.Equal(EmailTemplates.Names.ServiceRequestBackupClosed, toAlfa.Template);
        Assert.Contains("non risultava più libero", toAlfa.Content.HtmlBody);
    }

    [Fact]
    public async Task PrimaryRefuses_TheBackupThatAcceptedHasTheJobAtOnce_WithoutWaitingForTheHour()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);

        await s.Service.RejectAsync(primary.Id, s.SupplierOrgId, "Sono fuori sede");

        var winner = await s.ReadAsync(alfa.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, winner.Status);
        Assert.Equal(ServiceRequestGroupRole.PromotedBackup, winner.GroupRole);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(beta.Id)).Status);
        // It refused: the primary stays refused, and the host follows the backup that took the job.
        Assert.Equal(ServiceRequestStatus.Rifiutato, (await s.ReadAsync(primary.Id)).Status);
        var (items, _) = await s.Service.ListForHostAsync(new HostScope(s.HostOrgId), ServiceRequestRentalContext.ShortRent, null, null, null, 1, 50);
        Assert.Equal(alfa.Id, Assert.Single(items).Id);
    }

    // ─── Nobody accepted ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoBackupAccepted_TheSecondWindowOpens_TheFirstToTakeHasTheJob_AndTheOtherIsRefused()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        s.Clock.Advance(PrimaryWindow);
        s.ForgetNotifications();

        var opened = await Job(s).RunAsync();

        Assert.Equal(new ServiceRequestGroupRun(false, 1, 0, 1, 0, 0), opened);
        var group = await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync();
        Assert.Equal(ServiceRequestGroupPhase.BackupWindow, group.Phase);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(60), group.BackupDueAt);
        // The primary is still in the race, now without the priority; nobody is closed yet.
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(primary.Id)).Status);
        Assert.Equal(group.BackupDueAt, (await s.ReadAsync(primary.Id)).ResponseDueAt);
        Assert.Equal(2, s.Emails.Snapshot().Count(e => e.Template == EmailTemplates.Names.ServiceRequestBackupActive));

        // Two backups try to take it: one has it, the other is refused and closed.
        await s.Service.TakeAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);
        var refused = await Assert.ThrowsAnyAsync<DomainException>(() => s.Service.TakeAsync(beta.Id, beta.SupplierOrgId, beta.Member));
        Assert.Contains(refused.Code, new[] { ServiceRequestErrorCodes.StateChanged, ServiceRequestErrorCodes.InvalidTransition });

        var all = await s.Db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.GroupId == group.Id).ToListAsync();
        Assert.Equal(alfa.Id, Assert.Single(all, r => r.TakenAt != null).Id);
        Assert.Equal(ServiceRequestCancellationReasons.TakenByBackup, (await s.ReadAsync(beta.Id)).CancellationReason);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, (await s.ReadAsync(primary.Id)).CancellationReason);
        Assert.Equal(ServiceRequestGroupPhase.Closed, (await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).Phase);
    }

    [Fact]
    public async Task NobodyTakesTheJobInTheSecondWindow_EveryRequestLapses_AsASingleRequestDoes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        s.Clock.Advance(PrimaryWindow);
        await Job(s).RunAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(ServiceRequestGroupRun.Empty, await Job(s).RunAsync());
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        s.ForgetNotifications();

        var run = await Job(s).RunAsync();

        Assert.Equal(new ServiceRequestGroupRun(false, 1, 1, 0, 0, 0), run);
        foreach (var id in new[] { primary.Id, alfa.Id, beta.Id })
        {
            var saved = await s.ReadAsync(id);
            Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
            Assert.Equal(ServiceRequestActorParty.System, saved.CancelledBy);
            Assert.Equal(ServiceRequestCancellationReasons.NoResponse, saved.CancellationReason);
        }

        Assert.Equal(ServiceRequestGroupPhase.Closed, (await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).Phase);
        var toHost = Assert.Single(s.Emails.Snapshot(), e => e.To == "host@test.com");
        Assert.Contains("nessuna risposta", toHost.Content.Subject);
        // A run repeated finds nothing left, and tells nobody twice.
        var sent = s.Emails.Snapshot().Count;
        Assert.Equal(ServiceRequestGroupRun.Empty, await Job(s).RunAsync());
        Assert.Equal(sent, s.Emails.Snapshot().Count);
    }

    [Fact]
    public async Task TheLegacyAutoCancel_NeverClosesTheRequestsOfAGroup()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await CreateGroupAsync(s);
        s.Clock.Advance(TimeSpan.FromHours(5));

        var due = await ServiceRequestAutoCancelService.DueIdsOf(s.Db, s.Clock.GetUtcNow().UtcDateTime).ToListAsync();

        Assert.Empty(due);
    }

    // ─── The rules for the suppliers ─────────────────────────────────────────────────────────────────────────────────

    // ─── The first backup that accepts closes the race (decision of 2026-10-30) ──────────────────────────────────────

    [Fact]
    public async Task TheFirstBackupToAccept_ClosesTheRaceForTheOthers_InTheSameSave_AndTellsThemWithoutAPenalty()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(5));

        var accepted = await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);

        // Alfa is the only backup left: it waits for the primary, and its acceptance is on the record.
        Assert.Equal(ServiceRequestStatus.Richiesto, accepted.Status);
        var savedAlfa = await s.ReadAsync(alfa.Id);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, savedAlfa.BackupAcceptedAt);
        Assert.Equal(alfa.Member, savedAlfa.BackupAcceptedByUserId);
        Assert.Equal(ServiceRequestGroupRole.Backup, savedAlfa.GroupRole);

        // Beta's request is closed for good, by CasaZen, with its own reason, and the group goes on.
        var closed = await s.ReadAsync(beta.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, closed.Status);
        Assert.Equal(ServiceRequestActorParty.System, closed.CancelledBy);
        Assert.Equal(ServiceRequestCancellationReasons.BackupRaceClosed, closed.CancellationReason);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, closed.CancelledAt);
        Assert.Null(closed.ResponseDueAt);
        Assert.Null(closed.BackupAcceptedAt);
        Assert.Equal(ServiceRequestGroupPhase.PrimaryWindow, (await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).Phase);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(primary.Id)).Status);

        // Beta is told why, plainly, and nobody else is told anything: the primary and the host are not bothered.
        var toBeta = Assert.Single(s.Emails.Snapshot());
        Assert.Equal("beta-srl@test.com", toBeta.To);
        Assert.Equal(EmailTemplates.Names.ServiceRequestBackupClosed, toBeta.Template);
        Assert.Contains("già accettato", toBeta.Content.HtmlBody);
        Assert.Contains("nessuna penalità", toBeta.Content.HtmlBody);

        // It costs Beta nothing: the points of a supplier are written for what it did, and it did nothing wrong. Alfa answered in time.
        Assert.Empty(await s.ReliabilityEventsAsync(beta.SupplierOrgId));
        var reply = Assert.Single(await s.ReliabilityEventsAsync(alfa.SupplierOrgId));
        Assert.Equal(SupplierReliabilityEventKind.RepliedInTime, reply.Kind);
        Assert.True(reply.AsBackup);
    }

    [Fact]
    public async Task ABackupClosedByTheRace_CannotAcceptNorTake_Gets409WithItsOwnCode_AndLosesNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (_, alfa, beta) = await CreateGroupAsync(s);
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);

        var accept = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.AcceptAsBackupAsync(beta.Id, beta.SupplierOrgId, beta.Member));
        var take = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.TakeAsync(beta.Id, beta.SupplierOrgId, beta.Member));

        Assert.Equal(ServiceRequestGroupErrorCodes.BackupRaceClosed, accept.Code);
        Assert.Equal(ServiceRequestGroupErrorCodes.BackupRaceClosed, take.Code);
        // The request is as the race left it, and Alfa still holds the first place.
        var saved = await s.ReadAsync(beta.Id);
        Assert.Equal(ServiceRequestCancellationReasons.BackupRaceClosed, saved.CancellationReason);
        Assert.Null(saved.BackupAcceptedAt);
        Assert.NotNull((await s.ReadAsync(alfa.Id)).BackupAcceptedAt);
        Assert.Empty(await s.ReliabilityEventsAsync(beta.SupplierOrgId));
    }

    [Fact]
    public async Task TheBackupThatAccepted_HoldsItsTimeInItsAgenda_TheOthersNever_AndThePrimaryTakingFreesItAndReleasesTheBackup()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        var savedPrimary = await s.ReadAsync(primary.Id);
        var day = RomeCalendar.DateInRome(savedPrimary.ScheduledStartUtc!.Value);

        // Nobody has accepted yet: no backup holds anything.
        Assert.Empty(await s.Reader.ListForAgendaAsync(alfa.SupplierOrgId, day, day));

        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);

        // Alfa's agenda holds the time from now on (a request waiting counts); Beta, closed by the race, holds nothing.
        Assert.Single(await s.Reader.ListForAgendaAsync(alfa.SupplierOrgId, day, day));
        Assert.Empty(await s.Reader.ListForAgendaAsync(beta.SupplierOrgId, day, day));

        // The primary takes the job in its hour: Alfa is released, the time is free again and Alfa loses nothing.
        s.ForgetNotifications();
        await s.Service.TakeAsync(primary.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        Assert.Empty(await s.Reader.ListForAgendaAsync(alfa.SupplierOrgId, day, day));
        var released = await s.ReadAsync(alfa.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, released.Status);
        Assert.Equal(ServiceRequestCancellationReasons.PrimaryTookIt, released.CancellationReason);
        var toAlfa = Assert.Single(s.Emails.Snapshot(), e => e.To == "alfa-srl@test.com");
        Assert.Equal(EmailTemplates.Names.ServiceRequestBackupClosed, toAlfa.Template);
        Assert.Contains("di nuovo libero", toAlfa.Content.HtmlBody);
        Assert.Contains("nessuna penalità", toAlfa.Content.HtmlBody);
        Assert.All(await s.ReliabilityEventsAsync(alfa.SupplierOrgId), e => Assert.Equal(SupplierReliabilityEventKind.RepliedInTime, e.Kind));
        Assert.Empty(await s.ReliabilityEventsAsync(beta.SupplierOrgId));
    }

    [Fact]
    public async Task ABackupWhoseTimeIsNotFree_CannotAccept_409_AndTheRaceStaysOpenForTheNext()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        var savedPrimary = await s.ReadAsync(primary.Id);
        await s.SeedAsync(
            ServiceRequestStatus.PresoInCarico,
            r =>
            {
                r.ScheduledStartUtc = savedPrimary.ScheduledStartUtc;
                r.ScheduledEndUtc = savedPrimary.ScheduledEndUtc;
                r.TakenAt = ServiceRequestScenario.Instant.UtcDateTime;
            },
            supplierOrgId: alfa.SupplierOrgId);

        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member));

        // Alfa could not say yes, so nothing was closed: the race is open and Beta can still accept (which then closes it for Alfa).
        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, refused.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(alfa.Id)).Status);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(beta.Id)).Status);
        await s.Service.AcceptAsBackupAsync(beta.Id, beta.SupplierOrgId, beta.Member);
        Assert.Equal(ServiceRequestCancellationReasons.BackupRaceClosed, (await s.ReadAsync(alfa.Id)).CancellationReason);
    }

    [Fact]
    public async Task ABackupThatAcceptsAfterThePrimarysHourHasPassed_HasTheJobAtOnce()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        s.Clock.Advance(PrimaryWindow);

        // The job has not looked at the group yet: the acceptance does, and the primary is out of time.
        var result = await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, result.Status);
        Assert.Equal(ServiceRequestGroupRole.PromotedBackup, (await s.ReadAsync(alfa.Id)).GroupRole);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, (await s.ReadAsync(primary.Id)).CancellationReason);
        Assert.Equal(ServiceRequestCancellationReasons.BackupRaceClosed, (await s.ReadAsync(beta.Id)).CancellationReason);
        Assert.Equal(ServiceRequestGroupPhase.Closed, (await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).Phase);
    }

    // ─── Who earns and who loses points (SP-REL) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NobodyAnswers_ThePrimaryLosesTheBigShare_ASilentBackupTheSmallOne_AndWhoWasAwayLosesNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (_, alfa, beta) = await CreateGroupAsync(s);
        // Beta only works on Sundays: while the request waits (a Thursday) it could not have answered.
        await s.Agenda.ReplaceHoursAsync(
            beta.SupplierOrgId,
            new SupplierHoursInput([new SupplierHoursDayInput(DayOfWeek.Sunday, [new SupplierHoursBandInput(8 * 60, 9 * 60)])]));
        s.Clock.Advance(PrimaryWindow);
        await Job(s).RunAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(60));
        s.ForgetNotifications();

        await Job(s).RunAsync();

        var primaryMissed = Assert.Single(await s.ReliabilityEventsAsync());
        Assert.Equal(SupplierReliabilityEventKind.NoReplyInTime, primaryMissed.Kind);
        Assert.False(primaryMissed.AsBackup);
        var alfaMissed = Assert.Single(await s.ReliabilityEventsAsync(alfa.SupplierOrgId));
        Assert.Equal(SupplierReliabilityEventKind.NoReplyInTime, alfaMissed.Kind);
        Assert.True(alfaMissed.AsBackup);
        Assert.Empty(await s.ReliabilityEventsAsync(beta.SupplierOrgId));
        var points = await s.Kit.Reliability.PointsAsync([s.SupplierOrgId, alfa.SupplierOrgId, beta.SupplierOrgId]);
        Assert.Equal(-10, points[s.SupplierOrgId]);
        Assert.Equal(-2, points[alfa.SupplierOrgId]);
        Assert.False(points.ContainsKey(beta.SupplierOrgId));

        // The two that lost points are told why; the one that was away is told the request lapsed, and nothing about points.
        const string hint = "punteggio di affidabilità";
        Assert.Contains(hint, Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com" && e.Template == EmailTemplates.Names.ServiceRequestCancelledToSupplier).Content.HtmlBody);
        Assert.Contains(hint, Assert.Single(s.Emails.Snapshot(), e => e.To == "alfa-srl@test.com" && e.Template == EmailTemplates.Names.ServiceRequestCancelledToSupplier).Content.HtmlBody);
        Assert.DoesNotContain(hint, Assert.Single(s.Emails.Snapshot(), e => e.To == "beta-srl@test.com" && e.Template == EmailTemplates.Names.ServiceRequestCancelledToSupplier).Content.HtmlBody);
    }

    [Fact]
    public async Task ThePrimaryThatTakesTheJobInTheSecondWindow_AlreadyLostItsPoints_AndEarnsNoneForAnswerTooLate()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, _, _) = await CreateGroupAsync(s);
        s.Clock.Advance(PrimaryWindow);
        await Job(s).RunAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(10));

        await s.Service.TakeAsync(primary.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        // It missed its hour (written when the second window opened); taking the job later is not an answer in time.
        var missed = Assert.Single(await s.ReliabilityEventsAsync());
        Assert.Equal(SupplierReliabilityEventKind.NoReplyInTime, missed.Kind);
    }

    [Fact]
    public async Task ABackupThatRefusesInTime_AnswersInTime_AsABackup()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (_, alfa, beta) = await CreateGroupAsync(s);

        await s.Service.RejectAsync(alfa.Id, alfa.SupplierOrgId, "Siamo al completo");

        var reply = Assert.Single(await s.ReliabilityEventsAsync(alfa.SupplierOrgId));
        Assert.Equal(SupplierReliabilityEventKind.RepliedInTime, reply.Kind);
        Assert.True(reply.AsBackup);
        // Refusing did not close the race: Beta can still accept.
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(beta.Id)).Status);
    }

    [Fact]
    public async Task ABackup_CannotTakeTheJobWhileThePrimaryHasItsHour_ButCanAcceptAsABackup_Once()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.TakeAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member));
        Assert.Equal(ServiceRequestGroupErrorCodes.BackupWaitsForPrimary, refused.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(alfa.Id)).Status);
        var proposal = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.ProposeTimeAsync(
            alfa.Id,
            alfa.SupplierOrgId,
            alfa.Member,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14, ServiceRequestScenario.FridayAt14.AddHours(2))));
        Assert.Equal(ServiceRequestGroupErrorCodes.BackupCannotPropose, proposal.Code);

        s.Clock.Advance(TimeSpan.FromMinutes(7));
        var accepted = await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);
        var firstInstant = accepted.BackupAcceptedAt;
        s.Clock.Advance(TimeSpan.FromMinutes(7));
        var again = await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);

        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddMinutes(-7), firstInstant);
        Assert.Equal(firstInstant, again.BackupAcceptedAt);
        Assert.Equal(alfa.Member, (await s.ReadAsync(alfa.Id)).BackupAcceptedByUserId);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(alfa.Id)).Status);

        // Accepting as a backup is for a backup that waits: the primary's request is not one, nor is another supplier's.
        var notABackup = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.AcceptAsBackupAsync(primary.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId));
        Assert.Equal(ServiceRequestGroupErrorCodes.NotAWaitingBackup, notABackup.Code);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.AcceptAsBackupAsync(alfa.Id, s.SupplierOrgId, "auth0|x"));
    }

    [Fact]
    public async Task ABackup_SeesWhatASupplierSeesBeforeTheTake_NeverThePropertyNorTheHostNotes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);
        var savedPrimary = await s.ReadAsync(primary.Id);

        var view = await s.Reader.GetAsync(alfa.Id, alfa.SupplierOrgId);
        var (listed, _) = await s.Reader.ListAsync(alfa.SupplierOrgId, new SupplierInboxQuery([ServiceRequestStatus.Richiesto], null, null, 1, 20));

        Assert.NotNull(view);
        Assert.False(view.ContactDisclosed);
        Assert.Null(view.Notes);
        Assert.Null(view.Location.PropertyName);
        Assert.Null(view.Location.Address);
        Assert.Null(view.HostContact);
        Assert.Equal(ServiceRequestScenario.Comune, view.Location.City);
        Assert.Equal(savedPrimary.ScheduledStartUtc, view.Schedule.StartUtc);
        Assert.Equal(savedPrimary.ResponseDueAt, view.Schedule.RespondBy);
        var backup = Assert.IsType<SupplierJobBackup>(view.Backup);
        Assert.Equal(SupplierBackupRoles.Backup, backup.Role);
        Assert.Equal(SupplierBackupStates.Waiting, backup.State);
        Assert.Equal(savedPrimary.ResponseDueAt, backup.PrimaryDueAt);
        var inInbox = Assert.Single(listed);
        Assert.Null(inInbox.Notes);
        Assert.Null(inInbox.Location.PropertyName);

        // The chosen supplier sees its own request with the priority of its hour.
        var primaryView = await s.Reader.GetAsync(primary.Id, s.SupplierOrgId);
        Assert.Equal(SupplierBackupRoles.Primary, primaryView!.Backup!.Role);
        Assert.Equal(SupplierBackupStates.Priority, primaryView.Backup.State);

        // Once the backup has the job it is a request like any other: the property and the notes come with the take.
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);
        Assert.Equal(SupplierBackupStates.Accepted, (await s.Reader.GetAsync(alfa.Id, alfa.SupplierOrgId))!.Backup!.State);
        s.Clock.Advance(PrimaryWindow);
        await Job(s).RunAsync();
        var won = await s.Reader.GetAsync(alfa.Id, alfa.SupplierOrgId);
        Assert.NotNull(won);
        Assert.True(won.ContactDisclosed);
        Assert.Equal(ServiceRequestScenario.PropertyName, won.Location.PropertyName);
        Assert.Equal(ServiceRequestScenario.HostNotes, won.Notes);
        Assert.Equal(SupplierBackupStates.Won, won.Backup!.State);

        // The primary that did not answer has a cancelled request and no more to do with the group.
        var lost = await s.Reader.GetAsync(primary.Id, s.SupplierOrgId);
        Assert.Equal(ServiceRequestStatus.Annullato, lost!.Status);
        Assert.Null(lost.Backup);
    }

    [Fact]
    public async Task TheHostCancels_TheBackupsAreClosed_WithoutTheHostsWords()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        s.ForgetNotifications();

        await s.Service.CancelAsHostAsync(primary.Id, s.HostOrgId, "Il cliente ha disdetto, grazie");

        Assert.Equal(ServiceRequestGroupPhase.Closed, (await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).Phase);
        foreach (var backup in new[] { alfa, beta })
        {
            var saved = await s.ReadAsync(backup.Id);
            Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
            Assert.Equal(ServiceRequestCancellationReasons.RequestCancelledByHost, saved.CancellationReason);
        }

        var toBackups = s.Emails.Snapshot().Where(e => e.Template == EmailTemplates.Names.ServiceRequestBackupClosed).ToList();
        Assert.Equal(2, toBackups.Count);
        Assert.All(toBackups, email => Assert.DoesNotContain("disdetto", email.Content.Subject + email.Content.HtmlBody));
    }

    [Fact]
    public async Task APrimaryWithAProposedTimeIsTheHostsToAnswer_TheGroupDoesNotMoveUntilTheHostDecides()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);
        await s.Service.ProposeTimeAsync(
            primary.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14, ServiceRequestScenario.FridayAt14.AddHours(2)));
        s.Clock.Advance(PrimaryWindow + TimeSpan.FromMinutes(30));

        var waiting = await Job(s).RunAsync();

        // The window passed, but the supplier did answer: nobody is given the job over the host's head.
        Assert.Equal(ServiceRequestGroupRun.Empty with { Examined = 1 }, waiting);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(alfa.Id)).Status);

        // The host turns the time down: the hour starts again for the primary and the backups together.
        await s.Service.RejectProposalAsync(primary.Id, s.HostOrgId);
        var group = await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync();
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime + PrimaryWindow, group.PrimaryDueAt);
        Assert.Equal(group.PrimaryDueAt, (await s.ReadAsync(primary.Id)).ResponseDueAt);
        Assert.Equal(group.PrimaryDueAt, (await s.ReadAsync(alfa.Id)).ResponseDueAt);
        Assert.Equal(ServiceRequestGroupRun.Empty, await Job(s).RunAsync());
    }

    [Fact]
    public async Task AQuote_ABackupCannotSendOne_ThePrimarysFreezesTheGroup_AndTheHostAcceptingItClosesTheBackups()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, beta) = await CreateGroupAsync(s);
        List<ServiceRequestQuoteLine> lines = [new("Pulizia profonda", 30000)];

        // A backup answers the request as it is or refuses: the host would be answering a supplier it did not choose.
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.SendQuoteAsync(
            alfa.Id, alfa.SupplierOrgId, alfa.Member, new SendServiceRequestQuoteCommand(lines, null, null)));
        Assert.Equal(ServiceRequestGroupErrorCodes.BackupCannotQuote, refused.Code);

        // The primary's quote is the host's to answer: the hour passes and nobody is given the job over the host's head.
        await s.Service.AcceptAsBackupAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);
        var quoted = await s.Service.SendQuoteAsync(
            primary.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new SendServiceRequestQuoteCommand(lines, null, null));
        s.Clock.Advance(PrimaryWindow + TimeSpan.FromMinutes(30));
        Assert.Equal(ServiceRequestGroupRun.Empty with { Examined = 1 }, await Job(s).RunAsync());
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(alfa.Id)).Status);

        // The host accepts the quote: that is the primary's take, and the backups are closed.
        await s.Service.AcceptQuoteAsync(primary.Id, s.HostOrgId, quoted.QuoteSentAt!.Value);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await s.ReadAsync(primary.Id)).Status);
        // Alfa had accepted and is released because the primary has the job; Beta was closed with the race when Alfa accepted.
        var released = await s.ReadAsync(alfa.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, released.Status);
        Assert.Equal(ServiceRequestCancellationReasons.PrimaryTookIt, released.CancellationReason);
        var raced = await s.ReadAsync(beta.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, raced.Status);
        Assert.Equal(ServiceRequestCancellationReasons.BackupRaceClosed, raced.CancellationReason);

        Assert.Equal(ServiceRequestGroupPhase.Closed, (await s.Db.ServiceRequestGroups.AsNoTracking().SingleAsync()).Phase);
    }

    [Fact]
    public async Task ABackupTakingTheJobInTheSecondWindow_ClosesAPrimaryThatHasAQuoteWaiting_AndTheQuoteGoesWithIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (primary, alfa, _) = await CreateGroupAsync(s);
        s.Clock.Advance(PrimaryWindow);
        Assert.Equal(1, (await Job(s).RunAsync()).BackupWindowsOpened);
        List<ServiceRequestQuoteLine> lines = [new("Pulizia profonda", 30000)];
        await s.Service.SendQuoteAsync(
            primary.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new SendServiceRequestQuoteCommand(lines, null, null));

        await s.Service.TakeAsync(alfa.Id, alfa.SupplierOrgId, alfa.Member);

        // The database refuses a quote on a request that is not new: it is cleared with the cancellation.
        var lost = await s.ReadAsync(primary.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, lost.Status);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, lost.CancellationReason);
        Assert.Null(lost.QuoteSentAt);
    }

    [Fact]
    public async Task Job_RunsTheServiceOnce_AndWarnsOnlyWhenAGroupFailed()
    {
        var service = new Mock<IServiceRequestGroupRunService>();
        service.SetupSequence(s => s.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceRequestGroupRun(false, 3, 2, 1, 0, 0))
            .ReturnsAsync(new ServiceRequestGroupRun(false, 3, 1, 0, 1, 2))
            .ReturnsAsync(ServiceRequestGroupRun.LockTaken);
        var quiet = new CapturingLogger<Casazen.Web.BackgroundJobs.ServiceRequestGroupJob>();
        var noisy = new CapturingLogger<Casazen.Web.BackgroundJobs.ServiceRequestGroupJob>();

        await new Casazen.Web.BackgroundJobs.ServiceRequestGroupJob(service.Object, quiet).ExecuteAsync(CancellationToken.None);
        await new Casazen.Web.BackgroundJobs.ServiceRequestGroupJob(service.Object, noisy).ExecuteAsync(CancellationToken.None);
        await new Casazen.Web.BackgroundJobs.ServiceRequestGroupJob(service.Object, quiet).ExecuteAsync(CancellationToken.None);

        Assert.DoesNotContain(quiet.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
        var warning = Assert.Single(noisy.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("2 failed", warning.Message);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static ServiceRequestGroupRunService Job(ServiceRequestScenario s) =>
        new(s.Db, s.Service, NullLogger<ServiceRequestGroupRunService>.Instance, s.Clock);

    /// <summary>An active supplier of the scenario's comune and category: a possible backup. Its e-mail is its legal name, dashed.</summary>
    private static async Task<Guid> AddBackupAsync(ServiceRequestScenario s, string legalName)
    {
        var email = $"{legalName.Replace(' ', '-').ToLowerInvariant()}@test.com";
        var org = new Casazen.Core.Entities.Org
        {
            Name = legalName,
            Slug = $"bk-{Guid.NewGuid():N}"[..20],
            DisplayName = legalName,
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        s.Db.Orgs.Add(org);
        s.Db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = legalName,
            Phone = "+39 06 000000",
            Status = SupplierStatus.Active,
            ComuniJson = $"[\"{ServiceRequestScenario.Comune}\"]",
            CategoriesJson = "[\"cleaning\"]",
        });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        return org.Id;
    }

    /// <summary>The backups switched on and two backups in the comune, "Alfa Srl" and "Beta Srl".</summary>
    private static async Task<(Guid Alfa, Guid Beta)> AddTwoBackupsAsync(ServiceRequestScenario s)
    {
        s.Flags.Set(FeatureFlags.SupplierRequestBackups, true);
        return (await AddBackupAsync(s, "Alfa Srl"), await AddBackupAsync(s, "Beta Srl"));
    }

    /// <summary>A request of the host sent to the scenario's supplier and to two backups, "Alfa Srl" and "Beta Srl".</summary>
    private static async Task<(ServiceRequest Primary, BackupSide Alfa, BackupSide Beta)> CreateGroupAsync(ServiceRequestScenario s)
    {
        var (alfa, beta) = await AddTwoBackupsAsync(s);
        var primary = await CreateAsync(s, useBackups: null);

        var saved = await s.ReadAsync(primary.Id);
        var backups = await BackupsOfAsync(s, saved.GroupId!.Value);
        return (saved, new BackupSide(backups.Single(b => b.SupplierOrgId == alfa), alfa), new BackupSide(backups.Single(b => b.SupplierOrgId == beta), beta));
    }

    private static Task<ServiceRequest> CreateAsync(ServiceRequestScenario s, bool? useBackups) =>
        s.Service.CreateAsync(new CreateServiceRequestCommand(
            s.HostOrgId,
            ServiceRequestScenario.HostUserId,
            s.PropertyId,
            s.BookingId,
            s.SupplierOrgId,
            ServiceCategories.Cleaning,
            ServiceRequestUrgency.Normal,
            ServiceRequestScenario.HostNotes,
            ChargeToGuest: false,
            ServiceRequestRentalContext.ShortRent,
            ServiceListingId: null,
            ScheduledStartUtc: null,
            DurationMinutes: ServiceRequestScenario.ServiceMinutes,
            UseBackupSuppliers: useBackups));

    private static async Task<List<ServiceRequest>> BackupsOfAsync(ServiceRequestScenario s, Guid groupId) =>
        await s.Db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.GroupId == groupId && r.GroupRole == ServiceRequestGroupRole.Backup)
            .ToListAsync();

    private static async Task AssertSentAloneAsync(ServiceRequestScenario s, Guid requestId, TimeSpan window)
    {
        var saved = await s.ReadAsync(requestId);
        Assert.Null(saved.GroupId);
        Assert.Equal(ServiceRequestGroupRole.None, saved.GroupRole);
        Assert.Equal(saved.CreatedAt + window, saved.ResponseDueAt);
    }
}
